using System;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NukedOpl;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok35: stereo sampler pro přehrávání .mid souborů přes emulovaný OPL3 čip
    /// (SaxxonPike/NukedOpl, čistě C# port Nuked-OPL3) s GM instrumentální sadou
    /// z .op2 banky (DMX/Doom GENMIDI formát, viz Op2Bank) - "starý dobrý
    /// Sound Blaster" zvuk pro hrubou orientaci v prohlížeči souborů.
    ///
    /// Jedna instance = buď TÓNOVÝ engine (nástroj podle Program Change / nebo
    /// univerzální rejstřík, stejně jako Sf2MidiSynth - viz ISupportsMelodicRegister),
    /// nebo BICÍ engine (nástroj podle čísla MIDI noty, GM 35-81 - viz Op2Bank.
    /// PercussionForNote). O to, která instance dostane která MIDI data, se
    /// stará DualEngineMidiOut (bicí kanál D10 zvlášť), stejně jako u Sf2MidiSynth.
    ///
    /// ZJEDNODUŠENÍ v tomhle prvním kroku (jde doladit později, až se ověří,
    /// že základ zní dobře):
    ///  - Dvojhlasé ("double-voice") nástroje z .op2 banky hrají jen svým PRVNÍM
    ///    hlasem - druhý (detuned) hlas pro "tlustší" zvuk se zatím nepoužívá.
    ///  - Bicí hrají jako normální not(y) na volném kanálu, ne přes hardwarový
    ///    "rhythm mode" OPL čipu (registr 0xBD) - zvuk je ze stejné banky/dat,
    ///    jen bez specifického hardwarového omezení na 3 pevné bicí kanály.
    /// </summary>
    public sealed class OplMidiSynth : ISampleProvider, IMidiOut, ISupportsMelodicRegister
    {
        private const int Rate = 44100;
        private const int HwChannelCount = 18;   // OPL3 = 2x 9 kanálů (2-operátorový režim)
        private const float MasterGain = 0.5f;

        // Standardní rozestup registrů operátorů v rámci jedné "poloviny" OPL3
        // (9 kanálů) - viz Nuked-OPL3 zdroj (ad_slot) i klasická AdLib dokumentace.
        // Kanál 0-8 má operátor1 (modulátor) na jednom z těchhle posunů, operátor2
        // (nosič) vždy +3.
        private static readonly int[] Op1Offset = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };

        private readonly Opl3 _opl = new Opl3();
        private readonly Opl3Chip _chip = new Opl3Chip();
        private readonly Op2Bank _bank;
        private readonly bool _isDrumEngine;
        private readonly object _lock = new object();

        // krok35: stejný koncept jako Sf2MidiSynth - univerzální rejstřík (jen
        // u tónového enginu; u bicího enginu se ignoruje, bicí se vybírají
        // podle čísla MIDI noty, ne podle Program Change).
        private volatile Op2Instrument? _forcedMelodic;
        private readonly int[] _channelProgram = new int[16];

        private readonly int[] _volume = Enumerable128();
        private readonly int[] _expression = Enumerable128();
        private readonly double[] _bendCents = new double[16];

        private static int[] Enumerable128()
        {
            var a = new int[16];
            for (int i = 0; i < 16; i++) a[i] = 127;
            return a;
        }

        private sealed class Voice
        {
            public int HwChannel;
            public int MidiChannel;
            public int MidiNote;      // nota, kterou poslal přehrávač (NoteOff podle ní hledá)
            public int SoundingNote;  // skutečná výška, kterou hraje OPL (u bicích = Op2Instrument.Note)
            public Op2Instrument Instrument = null!;
            public int Velocity;      // krok35: uloženo kvůli pozdějšímu přepočtu hlasitosti (RefreshChannelLevels)
            public bool KeyDown = true;
        }

        private readonly List<Voice> _voices = new List<Voice>();
        private readonly bool[] _hwChannelUsed = new bool[HwChannelCount];

        private WaveOutEvent? _waveOut;
        private short[] _oplBuffer = Array.Empty<short>();

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public string Description { get; }

        private OplMidiSynth(Op2Bank bank, bool isDrumEngine, string description)
        {
            _bank = bank;
            _isDrumEngine = isDrumEngine;
            Description = description;

            _opl.Reset(_chip, Rate);
            _opl.WriteReg(_chip, 0x105, 0x01);   // OPL3 "new mode" - nutné pro stereo/širší vlnové tvary
            _opl.WriteReg(_chip, 0x104, 0x00);   // žádné 4-operátorové páry - jen 2-op, jako GM v .op2 bance
            _opl.WriteReg(_chip, 0x01, 0x20);    // povolit výběr vlnového tvaru (waveform select)
            for (int ch = 0; ch < 9; ch++)
            {
                // L+R zapnuté na obou polovinách, ať je vůbec něco slyšet (OPL3 new mode to vyžaduje).
                _opl.WriteReg(_chip, 0xC0 + ch, 0x30);
                _opl.WriteReg(_chip, 0x100 + 0xC0 + ch, 0x30);
            }
        }

        /// <summary>
        /// krok35: načte .op2 banku a vytvoří/rozběhne engine. isDrumEngine=true
        /// => bicí podle čísla noty (GM 35-81), jinak tónový engine podle
        /// Program Change / univerzálního rejstříku (viz SetUniversalProgram).
        /// </summary>
        public static OplMidiSynth? TryCreate(string op2Path, bool isDrumEngine, int universalProgram = 1)
        {
            Op2Bank bank;
            try { bank = Op2Bank.Load(op2Path); }
            catch { return null; }

            string description = $"{System.IO.Path.GetFileNameWithoutExtension(op2Path)} (OPL3{(isDrumEngine ? ", bicí" : "")})";
            var synth = new OplMidiSynth(bank, isDrumEngine, description);
            if (!isDrumEngine) synth.SetUniversalProgram(universalProgram);

            synth._waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 4 };
            synth._waveOut.Init(new SampleToWaveProvider16(synth));
            synth._waveOut.Play();
            return synth;
        }

        /// <summary>krok35: ISupportsMelodicRegister - u bicího enginu je to tiché no-op (bicí se vybírají podle noty, ne podle rejstříku).</summary>
        public void SetUniversalProgram(int universalProgram)
        {
            if (_isDrumEngine) return;

            if (universalProgram == 0) { _forcedMelodic = null; return; }
            int gmProgram = Math.Clamp(universalProgram, 1, 128) - 1;
            _forcedMelodic = _bank.Melodic[gmProgram];
        }

        // ------------------------------------------------------------------
        // MIDI události
        // ------------------------------------------------------------------
        public void SendEvent(MidiEvent midiEvent)
        {
            lock (_lock)
            {
                switch (midiEvent)
                {
                    case NoteOnEvent n when n.Velocity > 0:
                        StartNote((int)n.Channel, (int)n.NoteNumber, (int)n.Velocity);
                        break;

                    case NoteOnEvent n:   // velocity 0 = NoteOff
                        StopNote((int)n.Channel, (int)n.NoteNumber);
                        break;

                    case NoteOffEvent n:
                        StopNote((int)n.Channel, (int)n.NoteNumber);
                        break;

                    case ControlChangeEvent cc when (int)cc.ControlNumber == 7:
                        _volume[(int)cc.Channel] = (int)cc.ControlValue;
                        RefreshChannelLevels((int)cc.Channel);
                        break;

                    case ControlChangeEvent cc when (int)cc.ControlNumber == 11:
                        _expression[(int)cc.Channel] = (int)cc.ControlValue;
                        RefreshChannelLevels((int)cc.Channel);
                        break;

                    case PitchBendEvent pb:
                        _bendCents[(int)pb.Channel] = ((int)pb.PitchValue - 8192) / 8192.0 * 200.0;
                        RefreshChannelPitch((int)pb.Channel);
                        break;

                    case ProgramChangeEvent pc:
                        _channelProgram[(int)pc.Channel] = (int)pc.ProgramNumber;
                        break;
                }
            }
        }

        private Op2Instrument? GetInstrument(int channel, int note, out int soundingNote)
        {
            if (_isDrumEngine)
            {
                var drum = _bank.PercussionForNote(note);
                soundingNote = drum?.Note ?? note;
                return drum;
            }

            soundingNote = note;
            if (_forcedMelodic != null) return _forcedMelodic;
            return _bank.Melodic[Math.Clamp(_channelProgram[channel], 0, 127)];
        }

        private void StartNote(int channel, int note, int velocity)
        {
            var instrument = GetInstrument(channel, note, out int soundingNote);
            if (instrument == null) return;

            // Stejná nota na stejném kanálu znovu úderem - starou rychle ukončíme.
            for (int i = _voices.Count - 1; i >= 0; i--)
                if (_voices[i].MidiChannel == channel && _voices[i].MidiNote == note) ReleaseVoice(_voices[i], removeFromList: true);

            int hw = AllocateHwChannel();

            var voice = new Voice
            {
                HwChannel = hw,
                MidiChannel = channel,
                MidiNote = note,
                SoundingNote = soundingNote,
                Instrument = instrument,
                Velocity = velocity,
            };
            _voices.Add(voice);
            _hwChannelUsed[hw] = true;

            WriteVoicePatch(voice, velocity);
            WriteVoicePitch(voice);   // zapíše i KeyOn (bit 5 registru 0xB0) - viz voice.KeyDown
        }

        private void StopNote(int channel, int note)
        {
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var v = _voices[i];
                if (v.MidiChannel == channel && v.MidiNote == note && v.KeyDown)
                {
                    KeyOff(v.HwChannel);
                    v.KeyDown = false;
                    // Hlas se nechává doznít (release fáze obálky) - uvolní se hardwarový
                    // kanál, až na něj bude potřeba zahrát něco jiného (viz AllocateHwChannel).
                }
            }
        }

        private void ReleaseVoice(Voice v, bool removeFromList)
        {
            KeyOff(v.HwChannel);
            _hwChannelUsed[v.HwChannel] = false;
            if (removeFromList) _voices.Remove(v);
        }

        /// <summary>Najde volný hardwarový kanál (18 celkem); když žádný není, umlčí a vezme si nejstarší znějící hlas.</summary>
        private int AllocateHwChannel()
        {
            for (int i = 0; i < HwChannelCount; i++)
                if (!_hwChannelUsed[i]) return i;

            // Nejdřív zkusíme hlas, který už dozněl (KeyDown=false), pak nejstarší.
            Voice? victim = null;
            foreach (var v in _voices)
            {
                if (!v.KeyDown) { victim = v; break; }
                victim ??= v;
            }
            if (victim != null)
            {
                int hw = victim.HwChannel;
                ReleaseVoice(victim, removeFromList: true);
                return hw;
            }
            return 0;   // nemělo by nastat (_voices by muselo být prázdné i HwChannelCount==0)
        }

        private void RefreshChannelLevels(int channel)
        {
            foreach (var v in _voices)
                if (v.MidiChannel == channel) WriteVoiceLevel(v, v.Velocity);
        }

        private void RefreshChannelPitch(int channel)
        {
            foreach (var v in _voices)
                if (v.MidiChannel == channel) WriteVoicePitch(v);
        }

        // ------------------------------------------------------------------
        // Registry OPL3
        // ------------------------------------------------------------------
        private static (int Base, int Op1, int Op2) HwRegLayout(int hwChannel)
        {
            int half = hwChannel >= 9 ? 0x100 : 0x000;
            int local = hwChannel % 9;
            int op1 = Op1Offset[local];
            return (half, op1, op1 + 3);
        }

        private void WriteVoicePatch(Voice voice, int velocity)
        {
            var (half, op1, op2) = HwRegLayout(voice.HwChannel);
            var patchVoice = voice.Instrument.Voice1;

            WriteOperatorShape(half, op1, patchVoice.Modulator);
            WriteOperatorShape(half, op2, patchVoice.Carrier);
            _opl.WriteReg(_chip, half | (0xC0 + voice.HwChannel % 9), (byte)(patchVoice.Feedback | 0x30));

            WriteVoiceLevel(voice, velocity);
        }

        private void WriteOperatorShape(int half, int opOffset, Op2Operator op)
        {
            _opl.WriteReg(_chip, half | (0x20 + opOffset), op.TremVibr);
            _opl.WriteReg(_chip, half | (0x60 + opOffset), op.AttDec);
            _opl.WriteReg(_chip, half | (0x80 + opOffset), op.SustRel);
            _opl.WriteReg(_chip, half | (0xE0 + opOffset), op.Wave);
        }

        /// <summary>
        /// krok35: hlasitost = GM kvadratická křivka (stejná jako Sf2MidiSynth) z
        /// volume*expression*velocity, převedená na útlum v OPL "Total Level"
        /// jednotkách (0 = nejhlasitěji, 63 = ticho, cca 0,75 dB/jednotka).
        /// Aplikuje se jen na NOSIČ (carrier) - a na MODULÁTOR taky, pokud je
        /// zapojení "aditivní" (connection bit=1), protože tam přímo ovlivňuje
        /// hlasitost výstupu stejně jako nosič.
        /// </summary>
        private void WriteVoiceLevel(Voice voice, int velocity)
        {
            var (half, op1, op2) = HwRegLayout(voice.HwChannel);
            var patchVoice = voice.Instrument.Voice1;

            double vol = _volume[voice.MidiChannel] / 127.0;
            double expr = _expression[voice.MidiChannel] / 127.0;
            double velNorm = velocity / 127.0;
            double gain = Math.Max(1e-4, vol * vol * expr * expr * velNorm * velNorm);

            double attenDb = -20.0 * Math.Log10(gain);
            int attenUnits = (int)Math.Round(attenDb / 0.75);

            _opl.WriteReg(_chip, half | (0x40 + op2),
                (byte)((patchVoice.Carrier.Scale << 6) | Math.Clamp(patchVoice.Carrier.Level + attenUnits, 0, 63)));

            bool additive = (patchVoice.Feedback & 0x01) != 0;
            int modLevel = additive
                ? Math.Clamp(patchVoice.Modulator.Level + attenUnits, 0, 63)
                : patchVoice.Modulator.Level;
            _opl.WriteReg(_chip, half | (0x40 + op1), (byte)((patchVoice.Modulator.Scale << 6) | modLevel));
        }

        /// <summary>
        /// krok35: MIDI nota (+ pitch bend) -> OPL "block" (0-7) a "F-number"
        /// (0-1023), odvozeno přímo ze vztahu fáze/kmitočtu čipu
        /// (Freq = Fnum * 2^Block * OPL_RATE / 2^19 - viz Nuked-OPL3 zdroj).
        /// Hledá nejmenší "block", do kterého se F-number ještě vejde; u
        /// extrémně vysokých not (mimo běžný rozsah klaviatury) se ohraničí na
        /// block=7 / fnum=1023 místo přetečení.
        /// </summary>
        private static void ComputeBlockFnum(double freqHz, out int block, out int fnum)
        {
            for (block = 0; block < 7; block++)
            {
                double f = freqHz * Math.Pow(2, 19 - block) / Opl3.OPL_RATE;
                if (f < 1024) { fnum = (int)Math.Round(f); return; }
            }
            block = 7;
            fnum = 1023;
        }

        private double SoundingFreq(Voice voice)
        {
            double bendCents = _isDrumEngine ? 0.0 : _bendCents[voice.MidiChannel];
            return 440.0 * Math.Pow(2.0, (voice.SoundingNote - 69) / 12.0 + bendCents / 1200.0);
        }

        /// <summary>Zapíše F-number/block a bit KeyOn podle voice.KeyDown (NoteOn i živé přeladění za běhu - viz RefreshChannelPitch).</summary>
        private void WriteVoicePitch(Voice voice)
        {
            ComputeBlockFnum(SoundingFreq(voice), out int block, out int fnum);

            int ch = voice.HwChannel % 9;
            int half = voice.HwChannel >= 9 ? 0x100 : 0x000;
            _opl.WriteReg(_chip, half | (0xA0 + ch), (byte)(fnum & 0xFF));

            byte b0 = (byte)(((voice.KeyDown ? 1 : 0) << 5) | ((block & 0x07) << 2) | ((fnum >> 8) & 0x03));
            _opl.WriteReg(_chip, half | (0xB0 + ch), b0);
        }

        /// <summary>NoteOff: jen smaže bit KeyOn (block/fnum se dopočítá stejně, ale bez bitu 5).</summary>
        private void KeyOff(int hwChannel)
        {
            var voice = _voices.Find(v => v.HwChannel == hwChannel);
            if (voice == null) return;

            ComputeBlockFnum(SoundingFreq(voice), out int block, out int fnum);
            int ch = hwChannel % 9;
            int half = hwChannel >= 9 ? 0x100 : 0x000;
            byte b0 = (byte)(((block & 0x07) << 2) | ((fnum >> 8) & 0x03));
            _opl.WriteReg(_chip, half | (0xB0 + ch), b0);
        }

        // ------------------------------------------------------------------
        // Zvukový výstup
        // ------------------------------------------------------------------
        public int Read(float[] buffer, int offset, int count)
        {
            if (_oplBuffer.Length < count) _oplBuffer = new short[count];

            int pairs = count / 2;
            lock (_lock)
            {
                _opl.GenerateStream(_chip, _oplBuffer.AsSpan(0, pairs * 2), pairs);
            }

            for (int i = 0; i < pairs * 2; i++)
                buffer[offset + i] = (_oplBuffer[i] / 32768f) * MasterGain;

            return pairs * 2;
        }

        public void Dispose()
        {
            var waveOut = _waveOut;
            _waveOut = null;
            waveOut?.Stop();
            waveOut?.Dispose();
        }
    }
}
