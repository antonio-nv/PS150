using System;
using System.Collections.Generic;
using PS150.Core.ToneEngine;
using NAudio.Wave;

namespace PS150.Core.Output

{
    public class AudioEngine : IWaveProvider, IDisposable
    {
        private WaveOutEvent? _waveOut;
        private readonly PS150.Core.ToneEngine.ToneEngine _synth;

        // krok58: banka 8 (OPL3/SoundBlaster) pro ŽIVÉ hraní - samostatný
        // OplMidiSynth (NukedOpl), co sem nemixuje sám, ale necháme si od něj
        // každý buffer "vyzvednout" vzorky a přičíst je k varhanám (viz Read
        // níž). Je to STEREO engine, varhany jsou mono - prostě se obě strany
        // zprůměrují. Null, když se .op2 banka nenačetla (viz App.xaml.cs).
        private PS150.Core.Sampler.OplMidiSynth? _liveOpl;
        private float[] _liveOplBuffer = Array.Empty<float>();

        // krok61: banky, které (na rozdíl od Organ/SF2) hrají úplně MIMO ToneEngine
        // a navíc umí mít najednou aktivní jen JEDEN program (OPL3 čip / Windows
        // GS Wavetable, obojí přes jeden pevný MIDI kanál) - viz ToggleRegister
        // níž. Klíč = číslo banky, hodnota = "nastav tenhle program" (0 = žádný/
        // vypnuto). Naplňuje se zvenčí (App.xaml.cs) přes RegisterExclusiveBank,
        // AudioEngine sám neví nic o OplMidiSynth ani o Windows MIDI zařízeních.
        private readonly Dictionary<int, Action<int>> _exclusiveBankProgram = new Dictionary<int, Action<int>>();

        /// <summary>krok61: zaregistruje banku s "jen jeden program najednou" chováním (viz _exclusiveBankProgram výš).</summary>
        public void RegisterExclusiveBank(int bankNumber, Action<int> setProgram) => _exclusiveBankProgram[bankNumber] = setProgram;

        // krok66: pro tvrdé přepnutí banky (viz KillRegister/HardSwitchMelodicBank níž) -
        // u bank s vlastním živým enginem (OPL3/GM) nestačí poslat setProgram(0),
        // protože to samo o sobě NEumlčí už rozeznělé hlasy v tom engine (jen zruší
        // vynucený program pro PŘÍŠTÍ notu). Klíč = číslo banky, hodnota = "umlč
        // OKAMŽITĚ VŠECHNO, co v tobě teď zní". Naplňuje se zvenčí (App.xaml.cs).
        private readonly Dictionary<int, Action> _hardStopByBank = new Dictionary<int, Action>();

        /// <summary>krok66: zaregistruje "tvrdý stop" pro banku s vlastním živým enginem (viz _hardStopByBank výš).</summary>
        public void RegisterHardStop(int bankNumber, Action hardStop) => _hardStopByBank[bankNumber] = hardStop;

        // Špičková úroveň od posledního čtení (pro VU metr). Stejný princip
        // jako MaxLeftPeak/MaxRightPeak v AudioPlayer.cs.
        private float _peakSinceLastRead;

        public WaveFormat WaveFormat { get; }

        public AudioEngine(PS150.Core.ToneEngine.ToneEngine synth, int sampleRate = 44100)
        {
            _synth = synth;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1); // 44.1 kHz, Mono, 32-bit Float
        }

        /// <summary>krok58: zapojí živý OplMidiSynth (banka 8) do mixu - viz Read níž. Volá se jednou při startu (App.xaml.cs).</summary>
        public void AttachLiveOpl(PS150.Core.Sampler.OplMidiSynth? opl) => _liveOpl = opl;

        /// <summary>
        /// krok67: ladění/transpozice pro ŽIVÉ hraní - průchozí na ToneEngine (viz
        /// tamní komentář u LiveTransposeSemitones). Nezávislé na ladění přehrávače
        /// .mid (to je PlayerTuning, mimo AudioEngine úplně).
        /// </summary>
        public double LiveTransposeSemitones
        {
            get => _synth.LiveTransposeSemitones;
            set => _synth.LiveTransposeSemitones = value;
        }

        public double LiveReferenceA4Hz
        {
            get => _synth.LiveReferenceA4Hz;
            set => _synth.LiveReferenceA4Hz = value;
        }

        /// <summary>
        /// krok69: AJI pro ŽIVÉ hraní - nastavení (mutovatelná POCO, viz
        /// PS150.Core/02_ToneEngine/LiveAji.cs). Po úpravě libovolného pole zavolej
        /// ApplyLiveAji(), ať se to hned projeví i na právě podržených tónech.
        /// </summary>
        public PS150.Core.ToneEngine.LiveAjiSettings LiveAji => _synth.LiveAji;

        /// <summary>krok69: přepočte právě znějící tóny podle aktuálního LiveAji (po úpravě jeho nastavení).</summary>
        public void ApplyLiveAji() => _synth.RetuneActiveNotes();

        /// <summary>krok69: zahodí, co AutoDetect ví o dosavadním hraní (zapnutí/vypnutí, změna okna/kroku/prahu).</summary>
        public void ResetLiveAjiDetector() => _synth.ResetLiveAjiDetector();

        /// <summary>krok69: text pro obrazovku - viz ToneEngine.LiveAjiStatusText.</summary>
        public string LiveAjiStatusText() => _synth.LiveAjiStatusText();

        /// <summary>
        /// Spustí audio výstup na zvukovou kartu.
        /// </summary>
        public void Start()
        {
            if (_waveOut != null) return;

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100, // Nízká latence (50 ms) pro živé hraní bez zpoždění
                NumberOfBuffers = 4
            };

            _waveOut.Init(this);
            _waveOut.Play();
        }

        /// <summary>
        /// Zastaví audio výstup.
        /// </summary>
        public void Stop()
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
        }

        /// <summary>
        /// Callback od NAudio – plníme zvukový buffer vzorky z našeho PolySynthu.
        /// ZMĚNA: syntezátor je jediný mono, tři pásma jsou pryč. Dělení do pásem
        /// pro dvou/třípásmovou reproduktorovou soustavu přijde jako výhybka až
        /// na výstupu.
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int sampleCount = count / 4;
            var waveBuffer = new WaveBuffer(buffer);

            // krok58: živé OPL3 (banka 8) je STEREO ("pairs" L/R vzorků), vyzvedneme
            // ho jednou za celý buffer (stejná rychlost/hodiny jako tahle metoda -
            // obojí žene stejný WaveOutEvent) a zprůměrujeme na mono k varhanám.
            PS150.Core.Sampler.OplMidiSynth? liveOpl = _liveOpl;
            if (liveOpl != null)
            {
                int needed = sampleCount * 2;
                if (_liveOplBuffer.Length < needed) _liveOplBuffer = new float[needed];
                liveOpl.Read(_liveOplBuffer, 0, needed);
            }

            for (int i = 0; i < sampleCount; i++)
            {
                float sample = (float)_synth.GenerateNextSample();

                if (liveOpl != null)
                {
                    sample += (_liveOplBuffer[i * 2] + _liveOplBuffer[i * 2 + 1]) * 0.5f;
                }

                TrackPeak(ref _peakSinceLastRead, sample);

                waveBuffer.FloatBuffer[offset / 4 + i] = sample;
            }

            return count;
        }

        private static void TrackPeak(ref float peakStore, double sample)
        {
            float abs = (float)Math.Abs(sample);
            if (abs > peakStore) peakStore = abs;
        }

        /// <summary>
        /// Vrátí nejvyšší absolutní úroveň vzorku od posledního volání a vynuluje počítadlo.
        /// Určeno pro VU metr (stejný princip jako AudioPlayer.ReadPeakLevels() u MP3 přehrávače).
        /// </summary>
        public float ReadPeak()
        {
            float peak = _peakSinceLastRead;
            _peakSinceLastRead = 0f;
            return peak;
        }

        /// <summary>
        /// True, pokud poslední vygenerovaný vzorek přesáhl
        /// rozsah -1.0..1.0 (tedy došlo k oříznutí). Průchozí hodnota ze ToneEngine.
        /// </summary>
        public bool ClipDetected => _synth.ClipDetected;

        // --- Průchozí ovládání rejstříků (pro VGA panel / budoucí UI) ---

        /// <summary>Přepne rejstřík ON/OFF podle čísla. Vrací nový stav (true = ON).</summary>
        public bool ToggleRegister(int number) => _synth.ToggleRegister(number);

        /// <summary>
        /// krok15: přepnutí rejstříku podle (banka, číslo).
        /// krok58/61: banky z _exclusiveBankProgram (OPL3, Windows GM) jsou zvláštní
        /// případ - skutečný zvuk nehraje ToneEngine, ale samostatné "živé" zařízení,
        /// které navíc (na rozdíl od Organ/SF2 rejstříků) umí mít najednou zapnutý
        /// jen JEDEN program. Zapnutí rejstříku z takové banky proto nejdřív vypne
        /// případný jiný aktivní rejstřík ZE STEJNÉ banky (žádné tutti uvnitř ní) a
        /// pak pošle číslo dál; vypnutí pošle 0 (= žádný vynucený program). Ostatní
        /// banky (1, 3, 9...) tímhle vůbec neprocházejí.
        /// </summary>
        public bool ToggleRegister(PS150.Core.Input.RegisterId id)
        {
            bool nowOn = _synth.ToggleRegister(id);
            if (!_exclusiveBankProgram.TryGetValue(id.Bank, out var setProgram)) return nowOn;

            if (nowOn)
            {
                foreach (var other in _synth.ActiveRegisters)
                {
                    if (other.Bank == id.Bank && other.Number != id.Number) _synth.ToggleRegister(other);
                }
                setProgram(id.Number);
            }
            else
            {
                setProgram(0);
            }
            return nowOn;
        }

        public bool IsRegisterActive(PS150.Core.Input.RegisterId id) => _synth.IsRegisterActive(id);

        public IReadOnlyCollection<PS150.Core.Input.RegisterId> ActiveRegisters => _synth.ActiveRegisters;

        /// <summary>
        /// krok60 (oprava krok59 - ŠPATNĚ pochopené zadání): tutti/kombinování víc
        /// rejstříků NAJEDNOU má zůstat zachované, ale jen v rámci JEDNÉ banky -
        /// textové zadávání napříč různými bankami (Organ + SF2 + OPL3 současně)
        /// bylo nepohodlné a neintuitivní, a na skutečném nástroji bude stejně
        /// vždycky aktivní nejvýš jedna banka. Tahle metoda proto PŘED běžným
        /// ToggleRegister(id) vypne jen rejstříky z JINÝCH bank, než je id.Bank
        /// (bicí banka, drumBankNumber, se nedotýká vůbec - ta má svůj vlastní
        /// přepínač, viz Bic: řádek) - rejstříky ZE STEJNÉ banky jako id zůstanou
        /// beze změny, takže v rámci jedné banky jde dál kombinovat/tutti klidně
        /// všechno. Banka 8 (OPL3) má svoje vlastní omezení na jeden program
        /// najednou už v ToggleRegister výš (SetUniversalProgram) - tahle metoda
        /// do toho nijak nezasahuje, jen přidává tu banku do "vypni jiné banky".
        /// </summary>
        public bool ToggleMelodicRegister(PS150.Core.Input.RegisterId id, int drumBankNumber)
        {
            foreach (var other in ActiveRegisters)
            {
                if (other.Bank == drumBankNumber || other.Bank == id.Bank) continue;
                ToggleRegister(other);
            }

            return ToggleRegister(id);
        }

        /// <summary>
        /// krok70: pro rychlé "proklikání" celé banky klávesami +/- v poli Rej
        /// (viz Parameter.OnStepCommit/FileBrowserWindow) - na rozdíl od
        /// ToggleMelodicRegister/ToggleRegister (zadání čísla + Enter, umožňuje
        /// kombinovat/tutti víc rejstříků NAJEDNOU v jedné bance) tohle vždy
        /// nejdřív vypne všechny OSTATNÍ aktivní rejstříky ZE STEJNÉ banky jako
        /// id a pak zapne jen id - takže při držení +/- hraje v danou chvíli
        /// nejvýš jeden rejstřík z prohledávané banky. Jiné banky se nedotkne
        /// vůbec (o přepnutí banky se stará pole Banka/SelectBank).
        /// </summary>
        public void StepRegisterInBank(PS150.Core.Input.RegisterId id)
        {
            foreach (var other in ActiveRegisters)
            {
                if (other.Bank != id.Bank || other == id) continue;
                ToggleRegister(other);
            }

            if (!IsRegisterActive(id)) ToggleRegister(id);
        }

        /// <summary>
        /// krok66: na rozdíl od ToggleRegister(id) (rejstřík zhasne, ale rozeznělý
        /// tón nechá doznít přirozeným Release) tohle OKAMŽITĚ umlčí daný rejstřík
        /// bez dozvuku - viz ToneEngine.KillRegister a _hardStopByBank výš. Pro tvrdé
        /// přepnutí melodické banky (viz HardSwitchMelodicBank níž).
        /// </summary>
        public void KillRegister(PS150.Core.Input.RegisterId id)
        {
            _synth.KillRegister(id);
            if (_exclusiveBankProgram.TryGetValue(id.Bank, out var setProgram)) setProgram(0);
            if (_hardStopByBank.TryGetValue(id.Bank, out var hardStop)) hardStop();
        }

        /// <summary>
        /// krok66: tvrdé přepnutí melodické banky (viz FileBrowserWindow.SelectBank -
        /// volá se při změně pole "Banka", NE při zadání konkrétního rejstříku +
        /// Enter, to zůstává u ToggleMelodicRegister/měkkého odeznění). Umlčí BEZ
        /// DOZVUKU všechny aktivní melodické rejstříky napříč bankami (bicí banka
        /// se nedotýká) a pak - pokud newId není null (stejné číslo rejstříku
        /// existuje i v nové bance) - ho rovnou zapne. "Klavír č. 1" jedné banky tak
        /// zůstane "klavírem č. 1" i po přepnutí na jinou banku, beze změny čísla.
        /// </summary>
        public void HardSwitchMelodicBank(PS150.Core.Input.RegisterId? newId, int drumBankNumber)
        {
            foreach (var other in ActiveRegisters)
            {
                if (other.Bank == drumBankNumber) continue;
                KillRegister(other);
            }

            if (newId.HasValue) ToggleRegister(newId.Value);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
