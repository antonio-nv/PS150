using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NAudio.SoundFont;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// NOVÉ: diagnostika SoundFontu (SF2). Zapíše dva textové soubory vedle banky:
    ///   banka.sf2.info.txt        - přehled po presetech (klávesy, vrstvy rychlosti, smyčky, stereo)
    ///   banka.sf2.info_detail.txt - každá zóna zvlášť (rozsahy, vzorek, smyčka, kořenová nota)
    /// Podle výpisu se dá rozhodnout, jak psát přehrávač vzorků pro varhany, klavír a bicí.
    /// Spuštění: PS150.exe --sf2info "cesta\banka.sf2"
    /// </summary>
    public static class Sf2Info
    {
        public static string Write(string sf2Path)
        {
            string summaryPath = sf2Path + ".info.txt";
            string detailPath = sf2Path + ".info_detail.txt";

            var summary = new StringBuilder();
            var detail = new StringBuilder();

            try
            {
                long size = new FileInfo(sf2Path).Length;
                summary.AppendLine($"Soubor: {sf2Path}");
                summary.AppendLine($"Velikost: {size / 1048576.0:F1} MB");

                var sf = new SoundFont(sf2Path);

                var presets = sf.Presets.ToList();
                var instruments = sf.Instruments.ToList();
                var headers = sf.SampleHeaders.ToList();

                summary.AppendLine($"Presetů: {presets.Count}, nástrojů (instrument): {instruments.Count}, vzorků: {headers.Count}");

                double totalFrames = headers.Sum(h => (double)(h.End - h.Start));
                summary.AppendLine($"Vzorky celkem: {totalFrames / 1000000.0:F2} milionu vzorků (~{totalFrames * 2 / 1048576.0:F0} MB v paměti jako 16 bit)");
                summary.AppendLine($"Vzorkovací kmitočty: {string.Join(", ", headers.Select(h => (int)h.SampleRate).Distinct().OrderBy(x => x))}");
                summary.AppendLine();
                summary.AppendLine("Preset (banka:program) | zón | klávesy | vrstev rychlosti | se smyčkou | stereo vzorků | exkluzivní třídy | vzorků");
                summary.AppendLine(new string('-', 110));

                foreach (var preset in presets.OrderBy(p => p.Bank).ThenBy(p => p.PatchNumber))
                {
                    detail.AppendLine($"=== PRESET B{preset.Bank:D3} P{preset.PatchNumber:D3} \"{preset.Name}\"");

                    int zones = 0, looped = 0, stereo = 0;
                    int minKey = 127, maxKey = 0;
                    var velLayers = new HashSet<string>();
                    var exclusive = new SortedSet<int>();
                    var sampleNames = new HashSet<string>();

                    foreach (var presetZone in preset.Zones)
                    {
                        var instGen = Find(presetZone, GeneratorEnum.Instrument);
                        var instrument = instGen?.Instrument;
                        if (instrument == null) continue;

                        detail.AppendLine($"  instrument \"{instrument.Name}\"");

                        foreach (var zone in instrument.Zones)
                        {
                            var sampleGen = Find(zone, GeneratorEnum.SampleID);
                            var header = sampleGen?.SampleHeader;
                            if (header == null) continue;   // globální zóna nástroje

                            zones++;

                            var keyGen = Find(zone, GeneratorEnum.KeyRange);
                            var velGen = Find(zone, GeneratorEnum.VelocityRange);
                            var modeGen = Find(zone, GeneratorEnum.SampleModes);
                            var exclGen = Find(zone, GeneratorEnum.ExclusiveClass);
                            var rootGen = Find(zone, GeneratorEnum.OverridingRootKey);

                            int keyLo = keyGen != null ? keyGen.LowByteAmount : 0;
                            int keyHi = keyGen != null ? keyGen.HighByteAmount : 127;
                            int velLo = velGen != null ? velGen.LowByteAmount : 0;
                            int velHi = velGen != null ? velGen.HighByteAmount : 127;
                            int mode = modeGen != null ? modeGen.UInt16Amount : 0;
                            int root = rootGen != null && rootGen.Int16Amount >= 0 ? rootGen.Int16Amount : header.OriginalPitch;
                            int excl = exclGen != null ? exclGen.UInt16Amount : 0;

                            minKey = Math.Min(minKey, keyLo);
                            maxKey = Math.Max(maxKey, keyHi);
                            velLayers.Add($"{velLo}-{velHi}");
                            sampleNames.Add(header.SampleName);

                            bool isLooped = (mode & 1) == 1;
                            if (isLooped) looped++;
                            string link = header.SFSampleLink.ToString();
                            if (link.Contains("Left") || link.Contains("Right")) stereo++;
                            if (excl > 0) exclusive.Add(excl);

                            detail.AppendLine(
                                $"    klávesy {keyLo}-{keyHi}  rychlost {velLo}-{velHi}  kořen {root}  " +
                                $"vzorek \"{header.SampleName}\" {link}  {(int)header.SampleRate} Hz  " +
                                $"délka {header.End - header.Start}  smyčka {(isLooped ? $"{header.StartLoop - header.Start}..{header.EndLoop - header.Start}" : "ne")}  " +
                                $"režim {mode}  excl {excl}  jemné ladění {header.PitchCorrection}");
                        }
                    }

                    if (zones == 0) continue;

                    summary.AppendLine(
                        $"B{preset.Bank:D3}:P{preset.PatchNumber:D3} \"{preset.Name}\" | {zones} | {minKey}-{maxKey} | {velLayers.Count} | {looped}/{zones} | {stereo} | " +
                        $"{(exclusive.Count == 0 ? "-" : string.Join(",", exclusive))} | {sampleNames.Count}");
                }
            }
            catch (Exception ex)
            {
                summary.AppendLine();
                summary.AppendLine("CHYBA při čtení banky: " + ex.GetType().Name + ": " + ex.Message);
            }

            File.WriteAllText(summaryPath, summary.ToString(), new UTF8Encoding(true));
            File.WriteAllText(detailPath, detail.ToString(), new UTF8Encoding(true));
            return summaryPath;
        }

        private static Generator? Find(Zone zone, GeneratorEnum type)
        {
            if (zone?.Generators == null) return null;
            foreach (var g in zone.Generators)
            {
                if (g.GeneratorType == type) return g;
            }
            return null;
        }
    }
}
