using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PS150.Core.Banks;       // krok32: BankTable.Find - skutečný název banky pro Description
using PS150.Core.Generators;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok18: stereo sampler pro přehrávání .mid souborů z banky SF2 (MuseScore_General).
    /// Všechny kanály hrají klavír B000:P000 "Grand Piano", kanál 10 hraje bicí B128:P000 "Standard"
    /// (GM standardní souprava, hi-hat se vzájemně umlčuje přes exkluzivní třídy).
    ///
    /// Zpracovává to, co přehrávač posílá: NoteOn/Off, CC7 (hlasitost), CC11 (expression), CC10 (pan -
    /// ping-pong levý/pravý kanál zůstává), CC64 (pedál), CC120/123 (vše ticho), CC121 (reset),
    /// pitch bend (±2 půltóny; přes něj přehrávač dělá ladění a AJI). Program Change se ignoruje.
    ///
    /// Vlastní stereo výstup přes WaveOutEvent; výhledově půjde výstup do společné sběrnice s DSP.
    /// </summary>
    public sealed class Sf2MidiSynth : ISampleProvider, IMidiOut
    {
        private const int Rate = 44100;
        private const int DrumChannel = 9;
        private const int MaxVoices = 128;
        private const double BendRangeCents = 200.0;
        private const float MasterGain = 0.5f;

        // krok21: klavír z MuseScore je v bance o ~25 dB tišší než bicí (zóny mají velký útlum) - vyrovnání.
        private const double PianoMakeup = 8.0;

        // krok31: dřív natvrdo "klavír" (B000:P000) na všech netbicích kanálech.
        // Teď buď: _forcedMelodic != null => pořád jeden vynucený rejstřík na
        // všech netbicích kanálech (univerzální tónový rejstřík, viz
        // GmPianoMidiPlayer.SetMidiMelodicRegister), nebo _forcedMelodic == null
        // => každý kanál hraje svůj skutečný nástroj podle Program Change v
        // souboru (viz _channelProgram a GetMelodicPreset).
        private readonly Sf2Preset _defaultMelodic;              // B000:P000 - záloha, když se nenajde konkrétní rejstřík/program
        private readonly Sf2Preset? _forcedMelodic;               // null = respektuj Program Change ze souboru
        private readonly Dictionary<int, Sf2Preset> _melodicByProgram;   // B000:Pxxx podle GM programu (0-127), pro skutečné rejstříky
        private readonly int[] _channelProgram = new int[16];     // krok31: poslední Program Change na kanálu (jen když _forcedMelodic == null)
        private readonly Sf2Preset? _drums;
        private readonly object _lock = new object();
        private readonly List<Voice> _voices = new List<Voice>();

        private readonly int[] _volume = Enumerable.Repeat(127, 16).ToArray();
        private readonly int[] _expression = Enumerable.Repeat(127, 16).ToArray();
        private readonly int[] _pan = Enumerable.Repeat(64, 16).ToArray();
        private readonly double[] _bendCents = new double[16];
        private readonly bool[] _pedal = new bool[16];

        private WaveOutEvent? _waveOut;

        // krok22: měření chování v reálném čase - zapíše se do audio_diag.txt (vedle exe) při ukončení přehrávání.
        // Ukáže, jestli zvuková karta nedostává data včas (mezery mezi voláními Read), kolik času zabral Read
        // a SendEvent a kolik času spolkl sběr paměti (GC).
        private long _lastReadTicks;
        private long _reads, _lateReads;
        private double _maxGapMs, _maxReadMs, _maxSendMs, _bufferMs;
        private int _maxVoicesSeen;
        private long _nanCount, _clipCount;
        private readonly DateTime _createdAt = DateTime.Now;
        private readonly int _gc0Start = GC.CollectionCount(0), _gc1Start = GC.CollectionCount(1), _gc2Start = GC.CollectionCount(2);
        private readonly TimeSpan _gcPauseStart = GC.GetTotalPauseDuration();
        private readonly long _allocStart = GC.GetTotalAllocatedBytes();

        private sealed class Voice
        {
            public SampleVoice Sample = null!;
            public int Channel;
            public int Note;
            public int Velocity;   // krok27: síla úderu - viz gain v Read()
            public bool KeyDown = true;
            public bool PedalHeld;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        // krok32: dřív natvrdo "MuseScore_General (SF2)" bez ohledu na to, která
        // banka se SKUTEČNĚ načetla - takže přepnutí MidB/DrmB nebylo v
        // přehrávači vidět, ani když uvnitř fungovalo správně. Teď se text
        // sestaví v Create() podle skutečného čísla banky (viz BankTable).
        private readonly string _description;
        public string Description => _description;

        private Sf2MidiSynth(Sf2Preset defaultMelodic, Sf2Preset? drums,
            Dictionary<int, Sf2Preset> melodicByProgram, Sf2Preset? forcedMelodic, string description)
        {
            _defaultMelodic = defaultMelodic;
            _description = description;
            _drums = drums;
            _melodicByProgram = melodicByProgram;
            _forcedMelodic = forcedMelodic;
        }

        /// <summary>
        /// Vytvoří a rozběhne syntezátor z načtené banky SF2 (viz Sf2Loader.GetBank).
        /// Vrací null, když banka ještě není načtená nebo v ní chybí klavír B000:P000.
        ///
        /// krok31: universalProgram - univerzální tónový rejstřík pro VŠECHNY
        /// netbicí kanály, v "lidském" GM číslování 1-128 (1 = Acoustic Grand
        /// Piano, odpovídá starému natvrdo zapsanému chování). 0 = žádný
        /// univerzální rejstřík - hrají skutečné nástroje podle Program Change
        /// v souboru (viz GetMelodicPreset).
        /// </summary>
        public static Sf2MidiSynth? TryCreate(int bankNumber, int universalProgram = 1)
        {
            var bank = Sf2Loader.GetBank(bankNumber);
            if (bank == null) return null;
            return Create(bank, true, universalProgram, bankNumber);
        }

        /// <summary>krok20: syntezátor bez zvukové karty (pro diagnostické vykreslení do WAV, viz Sf2Render).</summary>
        public static Sf2MidiSynth? CreateOffline(Sf2Bank bank) => Create(bank, false, 1, null);

        // krok32: bankNumber jen kvůli popisku (Description) - viz BankTable.Find níž.
        // null, když ho nemáme po ruce (CreateOffline) - pak se vezme aspoň jméno souboru.
        private static Sf2MidiSynth? Create(Sf2Bank bank, bool play, int universalProgram, int? bankNumber)
        {
            var defaultMelodic = bank.Presets.FirstOrDefault(p => p.Bank == 0 && p.Program == 0);
            if (defaultMelodic == null) return null;

            var drums = bank.Presets.FirstOrDefault(p => p.Bank == 128 && p.Program == 0);

            // krok31: všechny melodické (B000) rejstříky podle GM programu - pro
            // skutečné nástroje ze souboru i pro vynucený univerzální rejstřík.
            var melodicByProgram = bank.Presets
                .Where(p => p.Bank == 0)
                .GroupBy(p => p.Program)
                .ToDictionary(g => g.Key, g => g.First());

            Sf2Preset? forcedMelodic = null;
            if (universalProgram != 0)
            {
                // "lidské" 1-128 (jak to zná organista z GM tabulky) -> MIDI program change 0-127.
                int gmProgram = Math.Clamp(universalProgram, 1, 128) - 1;
                forcedMelodic = melodicByProgram.TryGetValue(gmProgram, out var p) ? p : defaultMelodic;
            }

            string description = (bankNumber.HasValue ? BankTable.Find(bankNumber.Value)?.FullName : null)
                ?? Path.GetFileNameWithoutExtension(bank.Path);

            var synth = new Sf2MidiSynth(defaultMelodic, drums, melodicByProgram, forcedMelodic, $"{description} (SF2)");
            if (play)
            {
                // krok20: větší rezerva (150 ms) proti přerušování zvuku; u přehrávání souborů na latenci nezáleží.
                // krok22: výstup jako 16bitový PCM (stejná cesta jako u varhanního AudioEngine, který funguje),
                // rezerva 200 ms ve čtyřech bufferech.
                synth._waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 4 };
                synth._waveOut.Init(new SampleToWaveProvider16(synth));
                synth._waveOut.Play();
            }
            return synth;
        }

        // ------------------------------------------------------------------
        // MIDI události
        // ------------------------------------------------------------------
        public void SendEvent(MidiEvent midiEvent)
        {
            long sendStart = Stopwatch.GetTimestamp();
            try { SendEventCore(midiEvent); }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - sendStart) * 1000.0 / Stopwatch.Frequency;
                if (ms > _maxSendMs) _maxSendMs = ms;
            }
        }

        private void SendEventCore(MidiEvent midiEvent)
        {
            // krok20: začátek noty se připravuje MIMO zámek (první použití vzorku ho dekóduje - zvuková karta
            // mezitím nesmí čekat na zámek, jinak by zvuk přerušovalo).
            if (midiEvent is NoteOnEvent start && start.Velocity > 0)
            {
                StartNote((int)start.Channel, (int)start.NoteNumber, (int)start.Velocity);
                return;
            }

            lock (_lock)
            {
                switch (midiEvent)
                {
                    case NoteOnEvent on:   // sem se dostane jen velocity 0 = NoteOff
                        NoteOff((int)on.Channel, (int)on.NoteNumber);
                        break;

                    case NoteOffEvent off:
                        NoteOff((int)off.Channel, (int)off.NoteNumber);
                        break;

                    case ControlChangeEvent cc:
                        Control((int)cc.Channel, (int)cc.ControlNumber, (int)cc.ControlValue);
                        break;

                    case PitchBendEvent pb:
                        _bendCents[(int)pb.Channel] = ((int)pb.PitchValue - 8192) / 8192.0 * BendRangeCents;
                        break;

                    case ProgramChangeEvent pc:
                        // krok31: zajímá nás, jen když NENÍ vynucený univerzální rejstřík -
                        // viz GetMelodicPreset. Když je vynucený, hodnota se jen tiše uloží
                        // a nepoužije (neškodí, kdyby se univerzální rejstřík za běhu zrušil).
                        _channelProgram[(int)pc.Channel] = (int)pc.ProgramNumber;
                        break;

                    // Ostatní události (aftertouch, sysex...) se ignorují.
                }
            }
        }

        /// <summary>krok31: rejstřík pro netbicí kanál - buď vynucený univerzální, nebo podle posledního Program Change na tom kanálu.</summary>
        private Sf2Preset GetMelodicPreset(int channel)
        {
            if (_forcedMelodic != null) return _forcedMelodic;

            int program = _channelProgram[channel];   // viz pozn. u _bendCents - čtení bez zámku je tu neškodné
            return _melodicByProgram.TryGetValue(program, out var preset) ? preset : _defaultMelodic;
        }

        private void StartNote(int channel, int note, int velocity)
        {
            var preset = channel == DrumChannel ? _drums : GetMelodicPreset(channel);
            if (preset == null) return;

            var zones = preset.Match(note, velocity);
            if (zones.Count == 0) return;

            var sample = new SampleVoice(zones, Rate);   // tady se případně dekódují vzorky (mimo zámek)

            lock (_lock)
            {
                // Stejná nota na stejném kanálu se znovu úderem: starou rychle umlčíme.
                foreach (var v in _voices)
                    if (v.Channel == channel && v.Note == note && v.KeyDown) { v.Sample.Choke(); v.KeyDown = false; }

                sample.NoteOn();

                // Exkluzivní třída (hi-hat): nový úder umlčí ostatní hlasy stejné třídy na bicím kanálu.
                if (channel == DrumChannel && sample.ExclusiveClass > 0)
                {
                    foreach (var v in _voices)
                        if (v.Channel == DrumChannel && v.Sample.ExclusiveClass == sample.ExclusiveClass) v.Sample.Choke();
                }

                if (_voices.Count >= MaxVoices)
                {
                    _voices[0].Sample.Choke();
                    _voices.RemoveAt(0);
                }

                _voices.Add(new Voice { Sample = sample, Channel = channel, Note = note, Velocity = velocity });
            }
        }

        private void NoteOff(int channel, int note)
        {
            if (channel == DrumChannel) return;   // bicí jsou jednorázové, dohrají samy

            foreach (var v in _voices)
            {
                if (v.Channel != channel || v.Note != note || !v.KeyDown) continue;

                v.KeyDown = false;
                if (_pedal[channel]) v.PedalHeld = true;
                else v.Sample.NoteOff();
            }
        }

        private void Control(int channel, int number, int value)
        {
            switch (number)
            {
                case 7: _volume[channel] = value; break;
                case 11: _expression[channel] = value; break;
                case 10: _pan[channel] = value; break;

                case 64:
                    bool down = value >= 64;
                    _pedal[channel] = down;
                    if (!down)
                    {
                        foreach (var v in _voices)
                            if (v.Channel == channel && v.PedalHeld && !v.KeyDown) { v.PedalHeld = false; v.Sample.NoteOff(); }
                    }
                    break;

                case 120:   // All Sound Off
                    foreach (var v in _voices) if (v.Channel == channel) v.Sample.Choke();
                    break;

                case 123:   // All Notes Off
                    foreach (var v in _voices)
                        if (v.Channel == channel && v.KeyDown) { v.KeyDown = false; v.Sample.NoteOff(); }
                    break;

                case 121:   // Reset All Controllers
                    _expression[channel] = 127;
                    _pedal[channel] = false;
                    _bendCents[channel] = 0;
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Zvukový výstup (volá se z vlákna zvukové karty)
        // ------------------------------------------------------------------
        public int Read(float[] buffer, int offset, int count)
        {
            long readStart = Stopwatch.GetTimestamp();
            _bufferMs = count / 2 / (double)Rate * 1000.0;
            if (_lastReadTicks != 0)
            {
                double gap = (readStart - _lastReadTicks) * 1000.0 / Stopwatch.Frequency;
                if (gap > _maxGapMs) _maxGapMs = gap;
                if (gap > _bufferMs * 1.8) _lateReads++;   // karta si řekla o data pozdě = hrozí přerušení
            }
            _lastReadTicks = readStart;
            _reads++;

            Array.Clear(buffer, offset, count);
            int frames = count / 2;

            // krok26: zámek se drží jen na okamžik kopie seznamu hlasů - samotné
            // mixování (ta dražší část, až stovky hlasů × tisíce vzorků na buffer)
            // běží MIMO zámek. Dřív zámek svíral celé mixování, takže SendEvent
            // (NoteOn/Off/CC/pitch bend z přehrávacího vlákna DryWetMidi) musel
            // čekat, až mixování doběhne - při husté polyfonii to bylo klidně pár
            // ms, což se projevovalo jako zadrhnutí/poskočení v rytmu (noty samotné
            // pak dozněly správně, jen odstartovaly pozdě). Nově přidaný hlas se
            // do mixu dostane až od příštího Read (stejně jako dřív - na tom se
            // nic nemění), jen se zámek nedrží zbytečně dlouho.
            Voice[] snapshot;
            lock (_lock) { snapshot = _voices.ToArray(); }

            foreach (var v in snapshot)
            {
                int ch = v.Channel;

                // krok26: bicí kanál ignoruje ladění/pitch bend (jako na skutečném
                // GM zařízení) - jinak hraje mimo svou vlastní výšku a mění tak
                // i barvu bicí/činelového vzorku. Dřív se bend aplikoval i na bicí,
                // protože AJI/ladění posílá pitch bend na všechny kanály 0-15 včetně
                // desátého - to byl zdroj té "záhadné" tónové/bicí anomálie na
                // začátku souboru.
                double bend = ch == DrumChannel ? 0.0 : _bendCents[ch];
                double freq = 440.0 * Math.Pow(2.0, (v.Note - 69) / 12.0) * Math.Pow(2.0, bend / 1200.0);

                // krok27: velocity dřív ovlivňovala jen VÝBĚR zóny (Match), ne už
                // samotnou hlasitost - nota velocity 15 tak hrála stejně nahlas
                // jako velocity 74 ze stejné zóny. Teď škáluje gain stejnou
                // kvadratickou GM křivkou jako CC7/CC11. Mimo jiné díky tomu
                // zmizí i "probe" noty s velocity ~15, co některé nástroje
                // (třeba MuseScore export) nechávají na čase 0 každé stopy -
                // na skutečném GM zařízení zanikly samy, tady ne, protože jsme
                // jim bez téhle škály nechávali plnou hlasitost zóny.
                double vol = _volume[ch] / 127.0, expr = _expression[ch] / 127.0;
                double velNorm = v.Velocity / 127.0;
                double gain = vol * vol * expr * expr * velNorm * velNorm * MasterGain * (ch == DrumChannel ? 1.0 : PianoMakeup);   // GM křivka hlasitosti (kvadratická)

                double angle = _pan[ch] / 127.0 * (Math.PI / 2.0);    // pan se stálým výkonem
                float left = (float)(Math.Cos(angle) * gain);
                float right = (float)(Math.Sin(angle) * gain);

                for (int i = 0; i < frames; i++)
                {
                    float s = (float)v.Sample.GenerateSample(freq);
                    buffer[offset + 2 * i] += s * left;
                    buffer[offset + 2 * i + 1] += s * right;
                }
            }

            lock (_lock)
            {
                _voices.RemoveAll(v => v.Sample.IsFinished);
                if (_voices.Count > _maxVoicesSeen) _maxVoicesSeen = _voices.Count;
            }

            // měkké omezení špiček (aby hustá polyfonie necinkala přebuzením)
            for (int i = 0; i < count; i++)
            {
                float x = buffer[offset + i];
                if (float.IsNaN(x) || float.IsInfinity(x)) { _nanCount++; x = 0f; }
                else if (Math.Abs(x) > 1.0f) _clipCount++;
                buffer[offset + i] = (float)Math.Tanh(x);
            }

            double readMs = (Stopwatch.GetTimestamp() - readStart) * 1000.0 / Stopwatch.Frequency;
            if (readMs > _maxReadMs) _maxReadMs = readMs;

            return count;
        }

        public void Dispose()
        {
            var waveOut = _waveOut;
            _waveOut = null;
            if (waveOut != null)
            {
                try { waveOut.Stop(); } catch { }
                waveOut.Dispose();
            }

            lock (_lock) { _voices.Clear(); }

            WriteDiagnostics();
        }

        private void WriteDiagnostics()
        {
            try
            {
                if (_reads == 0) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== {_createdAt:yyyy-MM-dd HH:mm:ss}, trvání {(DateTime.Now - _createdAt).TotalSeconds:F1} s ===");
                sb.AppendLine($"Volání Read: {_reads}, buffer {_bufferMs:F1} ms");
                sb.AppendLine($"Největší mezera mezi voláními Read: {_maxGapMs:F1} ms; pozdních volání (> 1,8x buffer): {_lateReads}");
                sb.AppendLine($"Nejdelší Read: {_maxReadMs:F2} ms; nejdelší SendEvent (včetně čekání na zámek): {_maxSendMs:F2} ms");
                sb.AppendLine($"Nejvíc současných hlasů: {_maxVoicesSeen}; NaN/Inf vzorků: {_nanCount}; vzorků přes 1,0 před omezením: {_clipCount}");
                sb.AppendLine($"GC za dobu přehrávání: gen0 {GC.CollectionCount(0) - _gc0Start}, gen1 {GC.CollectionCount(1) - _gc1Start}, gen2 {GC.CollectionCount(2) - _gc2Start}; " +
                              $"pauzy GC celkem {(GC.GetTotalPauseDuration() - _gcPauseStart).TotalMilliseconds:F1} ms; alokováno {(GC.GetTotalAllocatedBytes() - _allocStart) / 1048576.0:F1} MB");
                sb.AppendLine($"GC režim latence: {System.Runtime.GCSettings.LatencyMode}");
                sb.AppendLine();
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "audio_diag.txt"), sb.ToString());
            }
            catch { /* diagnostika nesmí nic rozbít */ }
        }
    }
}
