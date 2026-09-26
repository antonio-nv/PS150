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

        // Exponent kompenzace podle počtu znějících hlasů (viz GenerateNextBandSample).
        private const double PolyphonyCompensationExponent = 0.5;

        public bool ClipDetected { get; private set; }

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

        public void NoteOn(int noteNumber)
        {
            lock (_lock)
            {
                foreach (int registerNumber in _activeRegisters)
                {
                    if (!_presetRegistry.TryGetValue(registerNumber, out var preset))
                        continue; // pro jistotu - nemělo by nastat

                    // OPRAVA: kmitočet se teď počítá ZVLÁŠŤ pro každý rejstřík,
                    // podle JEHO VLASTNÍ temperatury (preset.Temperament) - ne
                    // jednou společně pro všechny rejstříky najednou jako
                    // dřív. Díky tomu můžou dva rejstříky znít současně každý
                    // v jiném ladění (např. jeden rovnoměrně, druhý
                    // středotónově) - přesně jak to dělají skutečné varhany
                    // s víc temperovanými soustavami píšťal.
                    double freq = 440.0 * Math.Pow(2.0, (noteNumber - 69) / 12.0)
                                * Math.Pow(2.0, preset.Temperament.CentOffset(noteNumber) / 1200.0);

                    // Pokud tahle kombinace (nota + rejstřík) už hraje, jen ji
                    // znovu "nadechneme" (retrigger), nevytváříme duplicitní instanci.
                    var existing = _activeNotes.Find(n =>
                        n.NoteNumber == noteNumber && n.RegisterNumber == registerNumber);

                    if (existing != null)
                    {
                        existing.Voice.NoteOn();
                        existing.Frequency = freq;
                        continue;
                    }

                    var voice = CreateVoice(preset, noteNumber, _sampleRate);
                    voice.NoteOn();

                    _activeNotes.Add(new ActiveNote
                    {
                        NoteNumber = noteNumber,
                        RegisterNumber = registerNumber,
                        Frequency = freq,
                        Voice = voice
                    });
                }
            }
        }

        public void NoteOff(int noteNumber)
        {
            lock (_lock)
            {
                for (int i = 0; i < _activeNotes.Count; i++)
                {
                    if (_activeNotes[i].NoteNumber == noteNumber)
                    {
                        _activeNotes[i].Voice?.NoteOff();
                    }
                }
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

        public BandSample GenerateNextBandSample()
        {
            BandSample mixedSample = default;
            int voiceCount;

            lock (_lock)
            {
                voiceCount = _activeNotes.Count;

                for (int i = _activeNotes.Count - 1; i >= 0; i--)
                {
                    var activeNote = _activeNotes[i];

                    if (activeNote?.Voice != null)
                    {
                        BandSample sample = activeNote.Voice.GenerateSample(activeNote.Frequency);
                        mixedSample += sample;

                        if (activeNote.Voice.IsFinished)
                        {
                            _activeNotes.RemoveAt(i);
                        }
                    }
                }
            }

            // Preventivní kompenzace podle počtu znějících hlasů (viz dřívější
            // vysvětlení - N nezávislých zdrojů se sčítá jako √N, ne lineárně).
            // Stejný jeden koeficient se aplikuje na všechna tři pásma najednou.
            double compensation = voiceCount > 1
                ? 1.0 / Math.Pow(voiceCount, PolyphonyCompensationExponent)
                : 1.0;

            BandSample gained = mixedSample * (MasterGain * compensation);

            // Ořez se sleduje zvlášť pro každé pásmo - až každé pásmo poputuje
            // na svůj vlastní D/A převodník/zesilovač, může přebudit i jen jedno
            // z nich, zatímco ostatní budou v pořádku.
            ClipDetected =
                Math.Abs(gained.Bass) > 1.0 ||
                Math.Abs(gained.Mid) > 1.0 ||
                Math.Abs(gained.Treble) > 1.0;

            return new BandSample
            {
                Bass = Math.Clamp(gained.Bass, -1.0, 1.0),
                Mid = Math.Clamp(gained.Mid, -1.0, 1.0),
                Treble = Math.Clamp(gained.Treble, -1.0, 1.0)
            };
        }
    }
}
