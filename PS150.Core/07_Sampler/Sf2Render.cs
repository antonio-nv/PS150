using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NAudio.Wave;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok20: diagnostika přehrávače SF2. Vykreslí zkušební scénu (jedna nota klavíru, akord, bicí)
    /// do WAV (32bit float, stereo) a do textu zapíše parametry použitých zón (smyčka, režim, doznění)
    /// a rychlost vykreslování (kolikrát rychleji než v reálném čase, nejhorší blok).
    /// Spuštění: PS150.UI.Windows.exe --sf2render "MuseScore_General.sf2" "test.wav"
    /// </summary>
    public static class Sf2Render
    {
        public static string Run(string sf2Path, string wavPath, string? midiPath = null)
        {
            string reportPath = wavPath + ".txt";
            var sb = new StringBuilder();

            try
            {
                var sw = Stopwatch.StartNew();
                var bank = Sf2Bank.Load(sf2Path, null);
                sb.AppendLine($"Banka: {sf2Path}");
                sb.AppendLine($"Načtení banky: {sw.ElapsedMilliseconds} ms, presetů {bank.Presets.Count}");

                var piano = bank.Presets.FirstOrDefault(p => p.Bank == 0 && p.Program == 0);
                var drums = bank.Presets.FirstOrDefault(p => p.Bank == 128 && p.Program == 0);
                sb.AppendLine($"Klavír B000:P000: {(piano == null ? "CHYBÍ" : piano.Name + ", zón " + piano.Zones.Count)}");
                sb.AppendLine($"Bicí B128:P000: {(drums == null ? "CHYBÍ" : drums.Name + ", zón " + drums.Zones.Count)}");

                if (piano != null)
                {
                    foreach (var (note, vel) in new[] { (36, 100), (60, 100), (60, 40), (60, 70), (84, 100) })
                    {
                        sb.AppendLine();
                        sb.AppendLine($"--- zóny klavíru pro notu {note}, rychlost {vel} ---");
                        foreach (var z in piano.Match(note, vel))
                        {
                            sb.AppendLine(
                                $"klávesy {z.KeyLo}-{z.KeyHi} rychlost {z.VelLo}-{z.VelHi} kořen {z.RootKey} ladění {z.TuneCents:F1} ct " +
                                $"gain {z.Gain:F3} {z.SampleRate} Hz délka {z.Length} režim {z.Mode} smyčka {(z.Loops ? z.LoopStart + ".." + z.LoopEnd : "ne")} " +
                                $"stereo {z.Stereo} náběh {z.AttackSec:F3} s doznění {z.ReleaseSec:F3} s");
                        }
                    }
                }

                var synth = Sf2MidiSynth.CreateOffline(bank);
                if (synth == null) throw new InvalidOperationException("Syntezátor se nepodařilo vytvořit (chybí klavír).");

                // scéna: čas (s) -> událost
                var scene = new System.Collections.Generic.List<(double t, MidiEvent e)>();
                void On(double t, int ch, int note, int vel) => scene.Add((t, new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel) { Channel = (FourBitNumber)ch }));
                void Off(double t, int ch, int note) => scene.Add((t, new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0) { Channel = (FourBitNumber)ch }));

                double sceneSeconds = 11.5;

                if (midiPath == null)
                {
                    On(0.0, 0, 60, 100); Off(3.0, 0, 60);                       // jedna nota C4
                    int[] chord = { 48, 55, 60, 64, 67, 72 };                   // akord na šesti kanálech
                    for (int i = 0; i < chord.Length; i++) { On(4.0, i, chord[i], 90); Off(7.0, i, chord[i]); }
                    On(8.5, 9, 36, 110); On(9.0, 9, 38, 110); On(9.25, 9, 42, 100); On(9.5, 9, 46, 100); On(9.75, 9, 42, 100);
                }
                else
                {
                    // skutečný .mid soubor: noty, hlasitost, pedál, ohyb; panorama ping-pong podle kanálů
                    var midi = MidiFile.Read(midiPath, new ReadingSettings
                    {
                        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits
                    });
                    var tempoMap = midi.GetTempoMap();
                    var used = midi.GetTimedEvents().Select(te => te.Event).OfType<NoteOnEvent>().Select(n => (int)n.Channel).Distinct().OrderBy(c => c).ToArray();
                    for (int i = 0; i < used.Length; i++)
                        scene.Add((0.0, new ControlChangeEvent((SevenBitNumber)10, (SevenBitNumber)(used.Length > 1 ? (i % 2 == 0 ? 0 : 127) : 64)) { Channel = (FourBitNumber)used[i] }));
                    double last = 0;
                    foreach (var te in midi.GetTimedEvents())
                    {
                        double t = ((TimeSpan)te.TimeAs<MetricTimeSpan>(tempoMap)).TotalSeconds;
                        last = Math.Max(last, t);
                        if (te.Event is NoteOnEvent || te.Event is NoteOffEvent || te.Event is PitchBendEvent) scene.Add((t, te.Event));
                        else if (te.Event is ControlChangeEvent cc && cc.ControlNumber != 10) scene.Add((t, cc));
                    }
                    sceneSeconds = Math.Min(last + 3.0, 90.0);
                    sb.AppendLine($"MIDI soubor: {midiPath}, délka {last:F1} s, kanálů {used.Length}");
                }

                scene.Sort((a, b) => a.t.CompareTo(b.t));

                const int block = 512;
                const double rate = 44100.0;
                long totalFrames = (long)(sceneSeconds * rate);
                var buf = new float[block * 2];
                int next = 0;
                double worstMs = 0, sumMs = 0, worstSendMs = 0;
                int blocks = 0, overBudget = 0;
                float peak = 0;
                double budgetMs = block / rate * 1000.0;

                using (var writer = new WaveFileWriter(wavPath, synth.WaveFormat))
                {
                    for (long frame = 0; frame < totalFrames; frame += block)
                    {
                        double now = frame / rate;
                        while (next < scene.Count && scene[next].t <= now)
                        {
                            long s0 = Stopwatch.GetTimestamp();
                            synth.SendEvent(scene[next++].e);
                            worstSendMs = Math.Max(worstSendMs, (Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency);
                        }

                        long t0 = Stopwatch.GetTimestamp();
                        synth.Read(buf, 0, buf.Length);
                        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                        worstMs = Math.Max(worstMs, ms);
                        sumMs += ms;
                        blocks++;
                        if (ms > budgetMs) overBudget++;
                        foreach (var s in buf) peak = Math.Max(peak, Math.Abs(s));

                        writer.WriteSamples(buf, 0, buf.Length);
                    }
                }

                sb.AppendLine();
                sb.AppendLine("--- vykreslování ---");
                sb.AppendLine($"Délka scény: {totalFrames / rate:F1} s, bloků {blocks} po {block} vzorcích ({budgetMs:F1} ms)");
                sb.AppendLine($"Průměrná doba bloku: {sumMs / blocks:F2} ms, nejhorší: {worstMs:F2} ms, bloků delších než rozpočet: {overBudget}");
                sb.AppendLine($"Nejdelší SendEvent (začátek noty): {worstSendMs:F2} ms");
                sb.AppendLine($"Špička výstupu: {peak:F3}");
                if (midiPath == null)
                    sb.AppendLine("Scéna: 0-3 s nota C4 (klavír), 4-7 s akord C2 G2 C3 E3 G3 C4 na šesti kanálech, od 8,5 s bicí (36, 38, 42, 46, 42).");
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("CHYBA: " + ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
            }

            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(true));
            return reportPath;
        }
    }
}
