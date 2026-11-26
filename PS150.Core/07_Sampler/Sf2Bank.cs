using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.SoundFont;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok17: jedna zóna SF2 po slití všech úrovní (instrument + preset): rozsah kláves a rychlostí,
    /// kořenová nota, ladění (coarse + fine + korekce vzorku), útlum, smyčka, režim, exkluzivní třída,
    /// doba náběhu a doznění. Vzorek se převádí na float až při prvním použití.
    /// </summary>
    public sealed class Sf2Zone
    {
        public int KeyLo, KeyHi, VelLo, VelHi;
        public int RootKey;
        public double TuneCents;          // coarse*100 + fine + korekce vzorku (instrument i preset)
        public double Gain = 1.0;         // z InitialAttenuation (centibely)
        public int SampleRate;
        public int LoopStart, LoopEnd;    // relativně k začátku vzorku
        public int Mode;                  // 0 jednou, 1 smyčka, 3 smyčka do puštění a pak dohraje
        public int ExclusiveClass;        // 0 = žádná (hi-hat: otevřený/zavřený se navzájem umlčí)
        public bool Stereo;
        public float AttackSec = 0.002f, ReleaseSec = 0.01f;

        internal Sf2Bank Owner = null!;

        /// <summary>krok21: začátek vzorku a jeho délka v společné paměti banky (Pcm). Žádné kopie ani dekódování po zónách.</summary>
        public int Start;
        public int Length;

        public bool Loops => (Mode & 1) == 1 && LoopEnd > LoopStart + 1;

        /// <summary>Společné 16bitové vzorky celé banky (jedno pole, sdílené všemi zónami).</summary>
        public short[] Pcm => Owner.Pcm;
    }

    /// <summary>Jeden preset (banka MSB : program) se všemi zónami.</summary>
    public sealed class Sf2Preset
    {
        public int Bank { get; }       // MSB banky v SF2 (B000, B008, B128 = bicí)
        public int Program { get; }
        public string Name { get; }
        public List<Sf2Zone> Zones { get; } = new List<Sf2Zone>();

        public Sf2Preset(int bank, int program, string name)
        {
            Bank = bank; Program = program; Name = name;
        }

        /// <summary>Zóny, které zní pro danou notu a rychlost (víc zón = stereo pár nebo vrstvy).</summary>
        public List<Sf2Zone> Match(int note, int velocity)
        {
            var result = new List<Sf2Zone>();
            foreach (var z in Zones)
            {
                if (note >= z.KeyLo && note <= z.KeyHi && velocity >= z.VelLo && velocity <= z.VelHi)
                    result.Add(z);
            }
            return result;
        }
    }

    /// <summary>
    /// krok17: načtená banka SF2 (presety + zóny + surová data vzorků). Čte vlastnosti generátorů
    /// na úrovni instrumentu i presetu (ladění, útlum, rozsahy, obálka). Moduláory SF2 se nepoužívají.
    /// </summary>
    public sealed class Sf2Bank
    {
        public string Path { get; }
        public List<Sf2Preset> Presets { get; } = new List<Sf2Preset>();

        /// <summary>krok21: všechny vzorky banky jako jedno pole short (načte se jednou při načtení banky).</summary>
        public short[] Pcm { get; }

        private Sf2Bank(string path, short[] pcm)
        {
            Path = path;
            Pcm = pcm;
        }

        /// <param name="progress">Průběh 0..100 (čtení souboru ~40 %, zbytek skládání presetů).</param>
        public static Sf2Bank Load(string path, Action<int>? progress = null)
        {
            progress?.Invoke(2);
            var sf = new SoundFont(path);
            progress?.Invoke(40);

            // krok21: surová data se jednou překopírují do pole short a původní byte[] se uvolní.
            // Hlasy pak čtou přímo z tohoto pole - žádné dekódování ani alokace při úhozu
            // (dřív každý úder alokoval megabajty a vyvolával pauzy sběru paměti = praskání).
            byte[] raw = sf.SampleData;
            var pcm = new short[raw.Length / 2];
            Buffer.BlockCopy(raw, 0, pcm, 0, pcm.Length * 2);

            var bank = new Sf2Bank(path, pcm);
            var presets = sf.Presets.ToList();
            int done = 0;

            foreach (var p in presets)
            {
                var preset = bank.BuildPreset(p);
                if (preset.Zones.Count > 0) bank.Presets.Add(preset);

                done++;
                progress?.Invoke(40 + (int)(60.0 * done / Math.Max(1, presets.Count)));
            }

            progress?.Invoke(100);
            return bank;
        }

        private Sf2Preset BuildPreset(Preset p)
        {
            var result = new Sf2Preset(p.Bank, p.PatchNumber, (p.Name ?? "").Trim());

            Zone? presetGlobal = p.Zones.FirstOrDefault(z => Find(z, GeneratorEnum.Instrument) == null);

            foreach (var pz in p.Zones)
            {
                var instrument = Find(pz, GeneratorEnum.Instrument)?.Instrument;
                if (instrument == null) continue;

                Zone? instGlobal = instrument.Zones.FirstOrDefault(z => Find(z, GeneratorEnum.SampleID) == null);

                foreach (var iz in instrument.Zones)
                {
                    var header = Find(iz, GeneratorEnum.SampleID)?.SampleHeader;
                    if (header == null) continue;

                    var (ikl, ikh) = Range(GeneratorEnum.KeyRange, iz, instGlobal);
                    var (pkl, pkh) = Range(GeneratorEnum.KeyRange, pz, presetGlobal);
                    var (ivl, ivh) = Range(GeneratorEnum.VelocityRange, iz, instGlobal);
                    var (pvl, pvh) = Range(GeneratorEnum.VelocityRange, pz, presetGlobal);

                    int keyLo = Math.Max(ikl, pkl), keyHi = Math.Min(ikh, pkh);
                    int velLo = Math.Max(ivl, pvl), velHi = Math.Min(ivh, pvh);
                    if (keyLo > keyHi || velLo > velHi) continue;

                    // Součtové generátory: instrument + preset
                    int coarse = Amount(GeneratorEnum.CoarseTune, iz, instGlobal) + Amount(GeneratorEnum.CoarseTune, pz, presetGlobal);
                    int fine = Amount(GeneratorEnum.FineTune, iz, instGlobal) + Amount(GeneratorEnum.FineTune, pz, presetGlobal);
                    int atten = Amount(GeneratorEnum.InitialAttenuation, iz, instGlobal) + Amount(GeneratorEnum.InitialAttenuation, pz, presetGlobal);
                    int attackTc = Amount(GeneratorEnum.AttackVolumeEnvelope, iz, instGlobal, -12000) + Amount(GeneratorEnum.AttackVolumeEnvelope, pz, presetGlobal);
                    int releaseTc = Amount(GeneratorEnum.ReleaseVolumeEnvelope, iz, instGlobal, -12000) + Amount(GeneratorEnum.ReleaseVolumeEnvelope, pz, presetGlobal);

                    var modeGen = Gen(GeneratorEnum.SampleModes, iz, instGlobal);
                    var exclGen = Gen(GeneratorEnum.ExclusiveClass, iz, instGlobal);
                    var rootGen = Gen(GeneratorEnum.OverridingRootKey, iz, instGlobal);

                    int root = rootGen != null && rootGen.Int16Amount >= 0 ? rootGen.Int16Amount : header.OriginalPitch;
                    string link = header.SFSampleLink.ToString();

                    int sampleStart = (int)header.Start;
                    int sampleLength = (int)Math.Min((long)header.End - header.Start, (long)Pcm.Length - sampleStart);
                    if (sampleLength < 2) continue;

                    long loopStart = (long)header.StartLoop - header.Start;
                    long loopEnd = (long)header.EndLoop - header.Start;

                    result.Zones.Add(new Sf2Zone
                    {
                        KeyLo = keyLo, KeyHi = keyHi, VelLo = velLo, VelHi = velHi,
                        RootKey = root,
                        TuneCents = coarse * 100.0 + fine + header.PitchCorrection,
                        Gain = Math.Min(4.0, Math.Pow(10.0, -atten / 200.0)),
                        SampleRate = (int)header.SampleRate,
                        LoopStart = (int)Math.Max(0, loopStart),
                        LoopEnd = (int)Math.Min(Math.Max(0, loopEnd), sampleLength),
                        Start = sampleStart,
                        Length = sampleLength,
                        Mode = modeGen != null ? modeGen.UInt16Amount : 0,
                        ExclusiveClass = exclGen != null ? exclGen.UInt16Amount : 0,
                        Stereo = link.Contains("Left") || link.Contains("Right"),
                        AttackSec = (float)Math.Clamp(Math.Pow(2.0, attackTc / 1200.0), 0.002, 10.0),
                        ReleaseSec = (float)Math.Clamp(Math.Pow(2.0, releaseTc / 1200.0), 0.005, 20.0),
                        Owner = this
                    });
                }
            }

            return result;
        }

        // ---- pomocné čtení generátorů (zóna, jinak globální zóna) ----

        private static Generator? Find(Zone? zone, GeneratorEnum type)
        {
            if (zone?.Generators == null) return null;
            foreach (var g in zone.Generators)
                if (g.GeneratorType == type) return g;
            return null;
        }

        private static Generator? Gen(GeneratorEnum type, Zone zone, Zone? global) =>
            Find(zone, type) ?? Find(global, type);

        private static int Amount(GeneratorEnum type, Zone zone, Zone? global, int fallback = 0)
        {
            var g = Gen(type, zone, global);
            return g != null ? g.Int16Amount : fallback;
        }

        private static (int lo, int hi) Range(GeneratorEnum type, Zone zone, Zone? global)
        {
            var g = Gen(type, zone, global);
            return g != null ? (g.LowByteAmount, g.HighByteAmount) : (0, 127);
        }
    }
}
