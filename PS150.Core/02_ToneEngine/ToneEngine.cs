using System;
using System.Collections.Generic;
using System.Linq;
using PS150.Core.Generators;
using PS150.Core.Tones;
using PS150.Core.Input;

namespace PS150.Core.ToneEngine
{
    public class ActiveNote
    {
        public int NoteNumber { get; set; }     // MIDI číslo noty (např. 60 = C4)
        public int RegisterNumber { get; set; } // Které číslo rejstříku tenhle hlas hraje
        public double Frequency { get; set; }   // Kmitočet v Hz (např. 261.63 Hz)
        public SynthVoice Voice { get; set; }   // Instance hlasu (Organ/Piano/Cembalo/Bell)
        public InstrumentType Instrument { get; set; }   // NOVÉ: druh nástroje (bicí, klavír...) pro směrování a sustain
        public bool PendingRelease { get; set; }         // NOVÉ: klávesa puštěna při sešlápnutém pedálu - uvolní se až s pedálem
    }



    public class ToneEngine
    {

        private readonly double _sampleRate;

        // Seznam právě znějících hlasů - napříč VŠEMI aktivními rejstříky najednou
        private readonly List<ActiveNote> _activeNotes = new List<ActiveNote>();

        // Čísla rejstříků, které jsou právě "zataženy" (ON). Nová NoteOn vytvoří
        // hlas pro každý aktivní rejstřík zvlášť - stejná nota tak může znít
        // současně z víc rejstříků najednou (přesně jako u skutečných varhan).
        private readonly HashSet<int> _activeRegisters;

        // Zámek chránící sdílený stav před konfliktem MIDI vlákna a Audio vlákna
        private readonly object _lock = new object();

        // Základní zesílení výstupu. Žádné tvarování signálu (tanh apod.) záměrně
        // nepoužíváme - pro spektrální analýzu potřebujeme signál beze zkreslení.
        private const double MasterGain = 0.3;

        // Exponent kompenzace podle počtu znějících hlasů (viz GenerateNextSample).
        private const double PolyphonyCompensationExponent = 0.5;

        public bool ClipDetected { get; private set; }

        /// <summary>NOVÉ: MIDI kanál bicích (kanál 10 = index 9).</summary>
        public const int DrumChannel = 9;

        /// <summary>NOVÉ: číslo rejstříku bicí soupravy. Na kanálu 10 hraje VŽDY, ať je zapnutý nebo ne.</summary>
        public const int DrumRegisterNumber = 700;

        // NOVÉ: sustain pedál (CC64). Týká se jen klavírních hlasů (InstrumentType.Piano).
        private bool _sustainDown;

        // --- REGISTR PRESETŮ (jediné místo, kam se musí ručně přidat nový rejstřík) ---
        // Až vytvoříš nový preset, přidej ho SEM do seznamu, jinak ho ToggleRegister
        // podle čísla nenajde.
        private static readonly Dictionary<int, VoicePreset> _presetRegistry = BuildRegistry();

        private static Dictionary<int, VoicePreset> BuildRegistry()
        {
            var presets = new VoicePreset[]
            {
                _001_Bombard16.Preset,
                _002_ViolnBas16.Preset,
                _085_Aeolus.Preset,
                _200_Piano_Petrof.Preset,
                _201_Piano_Eletricke.Preset,
                _202_Piano_LimonadovyJoe.Preset,
                _300_Cembalo_RandallHopkirk.Preset,
                _500_Piano_Additivni.Preset,
                _501_Cembalo_Additivni.Preset,
                _400_Zvon_Zikmund.Preset,
                _600_Strings.Preset,
                _700_Drums.Preset,
            };

            var dict = new Dictionary<int, VoicePreset>();

            foreach (var preset in presets)
            {
                if (preset.Number <= 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ToneEngine] Preset '{preset.Name}' nemá platné Number ({preset.Number}) - přeskočen.");
                    continue;
                }

                if (dict.ContainsKey(preset.Number))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ToneEngine] Kolize čísla rejstříku {preset.Number} mezi '{dict[preset.Number].Name}' " +
                        $"a '{preset.Name}' - použit první, druhý ignorován.");
                    continue;
                }

                dict[preset.Number] = preset;
            }

            return dict;
        }

        // Parametr temperament tu zůstává kvůli zpětné kompatibilitě (kdyby ho
        // někde volalo starší UI), ale už se nepoužívá - ladění se teď určuje
        // ZVLÁŠŤ pro každý rejstřík podle VoicePreset.Temperament (viz NoteOn
        // níž), ne jednou společně pro celý engine.
        public ToneEngine(double sampleRate = 44100.0, Temperament? temperament = null)
        {
            _sampleRate = sampleRate;

            // Výchozí stav při startu aplikace - ať to hned po spuštění hraje
            // (jako doteď), ne že by bylo úplně potichu, dokud něco nezmáčkneš.
            _activeRegisters = new HashSet<int>();
            if (_presetRegistry.ContainsKey(1))
            {
                _activeRegisters.Add(1);
            }
        }

        // =========================================================================
        // OVLÁDÁNÍ REJSTŘÍKŮ (volá VGA panel / budoucí UI)
        // =========================================================================

        /// <summary>
        /// Přepne rejstřík podle čísla ON/OFF. Vrací true = teď je ON, false = teď
        /// je OFF nebo číslo vůbec neexistuje v registru presetů (neplatné číslo
        /// se prostě tiše ignoruje, nic nespadne).
        /// </summary>
        public bool ToggleRegister(int number)
        {
            if (!_presetRegistry.ContainsKey(number))
                return false;

            lock (_lock)
            {
                if (_activeRegisters.Contains(number))
                {
                    _activeRegisters.Remove(number);

                    // Vypnutí rejstříku = "nehraj už na něm nové tóny", ne "umlč
                    // násilně, co už zní". Necháme doznít přirozeným Release.
                    foreach (var note in _activeNotes)
                    {
                        if (note.RegisterNumber == number)
                        {
                            note.PendingRelease = false;
                            note.Voice.NoteOff();
                        }
                    }

                    return false;
                }
                else
                {
                    _activeRegisters.Add(number);
                    return true;
                }
            }
        }

        public bool IsRegisterActive(int number)
        {
            lock (_lock)
            {
                return _activeRegisters.Contains(number);
            }
        }

        public IReadOnlyCollection<int> ActiveRegisters
        {
            get
            {
                lock (_lock)
                {
                    return _activeRegisters.ToArray();
                }
            }
        }

        // =========================================================================
        // 1. REAKCE NA MIDI / KLÁVESNICI (Volá se při stisku a pustití klávesy)
        // =========================================================================

        /// <summary>
        /// ZMĚNA: směrování podle MIDI kanálu a dynamika.
        ///  - kanál 10 (index 9) hraje vždy bicí soupravu (rejstřík 700), číslo noty vybírá buben,
        ///  - ostatní kanály hrají zapnuté rejstříky kromě bicích,
        ///  - velocity (0..1) určuje hlasitost bicích, klavíru a dalších úhozových nástrojů
        ///    (varhany dynamiku nemají).
        /// </summary>
        public void NoteOn(int noteNumber, int channel = 0, double velocity = 1.0)
        {
            lock (_lock)
            {
                bool drumChannel = channel == DrumChannel;

                IEnumerable<int> registers;
                if (drumChannel)
                    registers = new[] { DrumRegisterNumber };
                else
                    registers = _activeRegisters.ToArray();

                foreach (int registerNumber in registers)
                {
                    if (!_presetRegistry.TryGetValue(registerNumber, out var preset))
                        continue; // pro jistotu - nemělo by nastat

                    // Bicí na melodických kanálech nehrají (zapnutý rejstřík 700 by jinak
                    // z každé klávesy dělal buben).
                    if (!drumChannel && preset.Instrument == InstrumentType.Drums)
                        continue;

                    // Kmitočet se počítá zvlášť pro každý rejstřík podle jeho vlastní
                    // temperatury (preset.Temperament).
                    double freq = 440.0 * Math.Pow(2.0, (noteNumber - 69) / 12.0)
                                * Math.Pow(2.0, preset.Temperament.CentOffset(noteNumber) / 1200.0);

                    double gain = VelocityGain(preset.Instrument, velocity);

                    // Pokud tahle kombinace (nota + rejstřík) už hraje, jen ji
                    // znovu "nadechneme" (retrigger), nevytváříme duplicitní instanci.
                    var existing = _activeNotes.Find(n =>
                        n.NoteNumber == noteNumber && n.RegisterNumber == registerNumber);

                    if (existing != null)
                    {
                        existing.Voice.Velocity = gain;
                        existing.PendingRelease = false;
                        existing.Voice.NoteOn();
                        existing.Frequency = freq;
                        continue;
                    }

                    var voice = CreateVoice(preset, noteNumber, _sampleRate);
                    voice.Velocity = gain;
                    voice.NoteOn();

                    _activeNotes.Add(new ActiveNote
                    {
                        NoteNumber = noteNumber,
                        RegisterNumber = registerNumber,
                        Frequency = freq,
                        Voice = voice,
                        Instrument = preset.Instrument
                    });
                }
            }
        }

        public void NoteOff(int noteNumber, int channel = 0)
        {
            // Bicí se NoteOff nezastavují - každý úder doznívá sám podle své obálky
            // (elektronické bicí posílají NoteOff hned po úderu a ten by zvuk useknul).
            if (channel == DrumChannel) return;

            lock (_lock)
            {
                for (int i = 0; i < _activeNotes.Count; i++)
                {
                    var active = _activeNotes[i];
                    if (active.NoteNumber != noteNumber || active.Instrument == InstrumentType.Drums) continue;

                    // Sešlápnutý pedál drží klavírní noty, uvolní se až s ním.
                    if (_sustainDown && active.Instrument == InstrumentType.Piano)
                        active.PendingRelease = true;
                    else
                        active.Voice?.NoteOff();
                }
            }
        }

        /// <summary>NOVÉ: pedál sustain (MIDI CC64). Platí jen pro klavírní rejstříky, varhany a ostatní ho ignorují.</summary>
        public void SetSustain(bool down)
        {
            lock (_lock)
            {
                _sustainDown = down;
                if (down) return;

                foreach (var active in _activeNotes)
                {
                    if (!active.PendingRelease) continue;
                    active.PendingRelease = false;
                    active.Voice?.NoteOff();
                }
            }
        }

        /// <summary>
        /// NOVÉ: hlasitost podle dynamiky úhozu. Bicí jdou přímo podle velocity (slabý úder = tichý),
        /// klavír a ostatní úhozové nástroje mají mírný základ, ať jemný úhoz úplně nezmizí.
        /// Varhany dynamiku nemají.
        /// </summary>
        private static double VelocityGain(InstrumentType instrument, double velocity)
        {
            velocity = Math.Clamp(velocity, 0.0, 1.0);

            switch (instrument)
            {
                case InstrumentType.Drums:
                    return velocity;
                case InstrumentType.Organ:
                    return 1.0;
                default:
                    return 0.25 + 0.75 * velocity;
            }
        }

        // Vybere správnou třídu hlasu podle preset.Instrument. Všechny čtyři třídy
        // dědí ze SynthVoice, takže se dají držet v jednom společném seznamu.
        private static SynthVoice CreateVoice(VoicePreset preset, int noteNumber, double sampleRate)
        {
            switch (preset.Instrument)
            {
                case InstrumentType.Piano:
                    return new PianoVoice(preset, sampleRate);
                case InstrumentType.Cembalo:
                    return new CembaloVoice(preset, sampleRate);
                case InstrumentType.Bell:
                    return new BellVoice(preset, sampleRate);
                case InstrumentType.AdditiveString:
                    return new AdditiveStringVoice(preset, sampleRate);
                case InstrumentType.Drums:
                    // Na rozdíl od ostatních hlasů potřebuje vědět KTEROU
                    // notu hrát (číslo noty = výběr zvuku, ne výška) - viz
                    // DrumVoice.cs a komentář u DrumParameter.
                    return new DrumVoice(preset, noteNumber, sampleRate);
                case InstrumentType.Organ:
                default:
                    return new OrganVoice(preset, sampleRate);
            }
        }

        // =========================================================================
        // 2. GENEROVÁNÍ ZVUKU PRO ZVUKOVKU (Volá audio karta v reálném čase)
        // =========================================================================

        /// <summary>
        /// ZMĚNA: jeden mono vzorek (dřív tři pásma). Rozdělení na hloubky/středy/výšky
        /// pro reproduktory patří až do výhybky na výstupu, ne do syntezátoru.
        /// </summary>
        public double GenerateNextSample()
        {
            double mixedSample = 0.0;
            int voiceCount;

            lock (_lock)
            {
                voiceCount = _activeNotes.Count;

                for (int i = _activeNotes.Count - 1; i >= 0; i--)
                {
                    var activeNote = _activeNotes[i];

                    if (activeNote?.Voice != null)
                    {
                        mixedSample += activeNote.Voice.GenerateSample(activeNote.Frequency);

                        if (activeNote.Voice.IsFinished)
                        {
                            _activeNotes.RemoveAt(i);
                        }
                    }
                }
            }

            // Preventivní kompenzace podle počtu znějících hlasů (N nezávislých
            // zdrojů se sčítá jako √N, ne lineárně).
            double compensation = voiceCount > 1
                ? 1.0 / Math.Pow(voiceCount, PolyphonyCompensationExponent)
                : 1.0;

            double gained = mixedSample * (MasterGain * compensation);

            ClipDetected = Math.Abs(gained) > 1.0;

            return Math.Clamp(gained, -1.0, 1.0);
        }
    }
}
