using System;
using System.Collections.Generic;
using System.Collections.Concurrent;   // krok17: registr se doplňuje i z vlákna načítání SF2
using System.Linq;
using PS150.Core.Generators;
using PS150.Core.Tones;
using PS150.Core.Input;
using PS150.Core.Banks; // krok15: banky
using PS150.Core.Sampler;   // krok17: SF2

namespace PS150.Core.ToneEngine
{
    public class ActiveNote
    {
        public int NoteNumber { get; set; }     // MIDI číslo noty (např. 60 = C4)
        public RegisterId Register { get; set; } // krok15: který rejstřík (banka + číslo) tenhle hlas hraje
        public double Frequency { get; set; }   // Kmitočet v Hz (např. 261.63 Hz)
        public SynthVoice Voice { get; set; }   // Instance hlasu (Organ/Piano/Cembalo/Bell)
        public InstrumentType Instrument { get; set; }   // NOVÉ: druh nástroje (bicí, klavír...) pro směrování a sustain
        public bool UsesSustain { get; set; }            // krok17: ovládá ho sustain pedál (klavíry)
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
        private readonly HashSet<RegisterId> _activeRegisters; // krok15: (banka, číslo) místo prostého čísla

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

        /// <summary>
        /// krok15: rejstřík bicí soupravy. Na kanálu 10 hraje VŽDY, ať je
        /// zapnutý nebo ne. krok40: bicí mají VLASTNÍ banku (9 - Syntetické
        /// bicí, viz Banky.cs _009_SynteticBici), nezávislou na bance 1
        /// (tóny) - stejně jako u jednoúčelového bicího modulu žádná
        /// melodická banka vedle sebe neexistuje, takže se mohlo číslo
        /// rejstříku přečíslovat z 700 na 1 (sedí teď i s názvem souboru
        /// PS150.Core/03_Percussion/_001_Drums.cs), beze strachu z kolize s
        /// Bombard16 (banka 1, taky číslo 1).
        /// </summary>
        public static readonly RegisterId DrumRegister = new RegisterId(_009_SynteticBici.Number, 1);

        // NOVÉ: sustain pedál (CC64). Týká se jen klavírních hlasů (InstrumentType.Piano).
        private bool _sustainDown;

        // --- REGISTR PRESETŮ (jediné místo, kam se musí ručně přidat nový rejstřík) ---
        // Až vytvoříš nový preset, přidej ho SEM do seznamu, jinak ho ToggleRegister
        // podle čísla nenajde.
        private static readonly ConcurrentDictionary<RegisterId, VoicePreset> _presetRegistry = new ConcurrentDictionary<RegisterId, VoicePreset>(BuildRegistry());

        // krok40: tóny (banka 1) a bicí (banka 9) jsou teď dvě NEZÁVISLÉ
        // číselné osy (viz Banky.cs _001_SyntetickeZvuky/_009_SynteticBici) -
        // RegisterPresets níž je spojuje dohromady do jednoho _presetRegistry,
        // ale kolize se hlídá jen uvnitř stejné banky, takže třeba Bombard16
        // (banka 1, číslo 1) a bicí (banka 9, číslo 1) si navzájem nepřekáží.
        private static Dictionary<RegisterId, VoicePreset> BuildRegistry()
        {
            var tonalPresets = new VoicePreset[]
            {
                _001_Bombard16.Preset,
                _002_ViolnBas16.Preset,
                _085_Aeolus.Preset,
                _108_VoxCelesta.Preset,
                _200_Piano_Petrof.Preset,
                _201_Piano_Eletricke.Preset,
                _202_Piano_LimonadovyJoe.Preset,
                _300_Cembalo_RandallHopkirk.Preset,
                _500_Piano_Additivni.Preset,
                _501_Cembalo_Additivni.Preset,
                _400_Zvon_Zikmund.Preset,
                _600_Strings.Preset,
            };

            var drumPresets = new VoicePreset[]
            {
                _001_Drums.Preset,   // krok40: banka 9 (Syntetické bicí), viz PS150.Core/03_Percussion/_001_Drums.cs
            };

            var dict = new Dictionary<RegisterId, VoicePreset>();
            RegisterPresets(dict, _001_SyntetickeZvuky.Number, tonalPresets);
            RegisterPresets(dict, _009_SynteticBici.Number, drumPresets);
            return dict;
        }

        /// <summary>
        /// krok40: zaregistruje seznam presetů do JEDNÉ konkrétní banky -
        /// volá se zvlášť pro tóny (banka 1) a zvlášť pro bicí (banka 9),
        /// takže se kolize čísel hlídá jen uvnitř stejné banky, nikdy napříč
        /// (viz BuildRegistry výš).
        /// </summary>
        private static void RegisterPresets(Dictionary<RegisterId, VoicePreset> dict, int bank, VoicePreset[] presets)
        {
            foreach (var preset in presets)
            {
                if (preset.Number <= 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ToneEngine] Preset '{preset.Name}' nemá platné Number ({preset.Number}) - přeskočen.");
                    continue;
                }

                var id = new RegisterId(bank, preset.Number);

                if (dict.ContainsKey(id))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ToneEngine] Kolize rejstříku {id} mezi '{dict[id].Name}' " +
                        $"a '{preset.Name}' - použit první, druhý ignorován.");
                    continue;
                }

                dict[id] = preset;
            }
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
            _activeRegisters = new HashSet<RegisterId>();
            var startId = new RegisterId(_001_SyntetickeZvuky.Number, 1);
            if (_presetRegistry.ContainsKey(startId))
            {
                _activeRegisters.Add(startId);
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
        /// <summary>
        /// krok17: číslo rejstříku pro preset z SF2 = MSB*1000 + program (B000 P040 = 40, B008 P040 = 8040);
        /// bicí kity (B128) mají 9000 + program. Čísla odpovídají výpisu v .info.txt banky.
        /// </summary>
        public static int Sf2RegisterNumber(int msb, int program) => msb == 128 ? 9000 + program : msb * 1000 + program;

        /// <summary>krok17: zapíše presety načtené banky SF2 do registru. Vrací počet přidaných rejstříků.</summary>
        public static int RegisterSf2Bank(int bankNumber, Sf2Bank bank)
        {
            int count = 0;
            foreach (var p in bank.Presets)
            {
                int number = Sf2RegisterNumber(p.Bank, p.Program);
                var preset = new VoicePreset
                {
                    Name = p.Name,
                    Number = number,
                    Instrument = InstrumentType.Sample,
                    Sf2Preset = p,
                    SustainPedal = p.Bank == 0 && p.Program < 8   // klavíry (GM 0-7) ovládá pedál
                };
                if (_presetRegistry.TryAdd(new RegisterId(bankNumber, number), preset)) count++;
            }
            return count;
        }

        // krok16: dotazy na registr pro formulář (banky, čísla rejstříků v bance, jméno rejstříku).

        /// <summary>Čísla bank, které mají aspoň jeden rejstřík (vzestupně). Později se omezí nastavením povolených bank.</summary>
        public static IReadOnlyList<int> Banks =>
            _presetRegistry.Keys.Select(k => k.Bank).Distinct().OrderBy(b => b).ToList();

        /// <summary>Čísla rejstříků v dané bance (vzestupně).</summary>
        public static IReadOnlyList<int> NumbersInBank(int bank) =>
            _presetRegistry.Keys.Where(k => k.Bank == bank).Select(k => k.Number).OrderBy(n => n).ToList();

        /// <summary>Jméno rejstříku nebo null, když v registru není.</summary>
        public static string? RegisterName(RegisterId id) =>
            _presetRegistry.TryGetValue(id, out var preset) ? preset.Name : null;

        public static bool RegisterExists(RegisterId id) => _presetRegistry.ContainsKey(id);

        /// <summary>
        /// krok15: zpětně kompatibilní zadání jen čísla (bez banky). Najde banku, ve které
        /// číslo existuje (při shodě vyhraje nižší číslo banky). Do formuláře s bankami
        /// (krok16) se tohle nahradí explicitní volbou banky.
        /// </summary>
        public bool TryResolveRegister(int number, out RegisterId id)
        {
            bool found = false;
            id = default;
            foreach (var key in _presetRegistry.Keys)
            {
                if (key.Number != number) continue;
                if (!found || key.Bank < id.Bank) { id = key; found = true; }
            }
            return found;
        }

        public bool ToggleRegister(int number)
        {
            return TryResolveRegister(number, out var id) && ToggleRegister(id);
        }

        public bool ToggleRegister(RegisterId number)
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
                        if (note.Register == number)
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

        public bool IsRegisterActive(RegisterId number)
        {
            lock (_lock)
            {
                return _activeRegisters.Contains(number);
            }
        }

        public IReadOnlyCollection<RegisterId> ActiveRegisters
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
        ///  - kanál 10 (index 9) hraje vždy bicí soupravu (DrumRegister, banka 9 - viz Banky.cs), číslo noty vybírá buben,
        ///  - ostatní kanály hrají zapnuté rejstříky kromě bicích,
        ///  - velocity (0..1) určuje hlasitost bicích, klavíru a dalších úhozových nástrojů
        ///    (varhany dynamiku nemají).
        /// </summary>
        public void NoteOn(int noteNumber, int channel = 0, double velocity = 1.0)
        {
            lock (_lock)
            {
                bool drumChannel = channel == DrumChannel;

                IEnumerable<RegisterId> registers;
                if (drumChannel)
                    registers = new[] { DrumRegister };
                else
                    registers = _activeRegisters.ToArray();

                foreach (var registerNumber in registers)
                {
                    if (!_presetRegistry.TryGetValue(registerNumber, out var preset))
                        continue; // pro jistotu - nemělo by nastat

                    // Bicí na melodických kanálech nehrají (zapnutý bicí rejstřík by jinak
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
                        n.NoteNumber == noteNumber && n.Register == registerNumber);

                    // krok17: vzorkový hlas se při opakovaném úderu neretriggeruje, starý se rychle umlčí a začne nový
                    // (jiná rychlost úhozu = jiná vrstva vzorku).
                    if (existing != null && preset.Instrument == InstrumentType.Sample)
                    {
                        (existing.Voice as SampleVoice)?.Choke();
                        existing = null;
                    }

                    if (existing != null)
                    {
                        existing.Voice.Velocity = gain;
                        existing.PendingRelease = false;
                        existing.Voice.NoteOn();
                        existing.Frequency = freq;
                        continue;
                    }

                    var voice = CreateVoice(preset, noteNumber, velocity, _sampleRate);
                    voice.Velocity = gain;
                    voice.NoteOn();

                    // krok17: exkluzivní třída SF2 (hi-hat): nový úder umlčí ostatní hlasy stejné třídy v tomto rejstříku
                    if (voice is SampleVoice sampleVoice && sampleVoice.ExclusiveClass > 0)
                    {
                        foreach (var other in _activeNotes)
                        {
                            if (other.Register == registerNumber && other.NoteNumber != noteNumber
                                && other.Voice is SampleVoice ov && ov.ExclusiveClass == sampleVoice.ExclusiveClass)
                                ov.Choke();
                        }
                    }

                    _activeNotes.Add(new ActiveNote
                    {
                        NoteNumber = noteNumber,
                        Register = registerNumber,
                        Frequency = freq,
                        Voice = voice,
                        UsesSustain = preset.Instrument == InstrumentType.Piano || preset.SustainPedal,
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
                    if (_sustainDown && active.UsesSustain)
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
                case InstrumentType.Sample:
                    return 1.0;   // krok17: dynamiku řeší vrstvy rychlosti ve vzorcích
                default:
                    return 0.25 + 0.75 * velocity;
            }
        }

        // Vybere správnou třídu hlasu podle preset.Instrument. Všechny čtyři třídy
        // dědí ze SynthVoice, takže se dají držet v jednom společném seznamu.
        private static SynthVoice CreateVoice(VoicePreset preset, int noteNumber, double velocity, double sampleRate)
        {
            switch (preset.Instrument)
            {
                case InstrumentType.Sample:
                    {
                        int vel127 = Math.Clamp((int)Math.Round(velocity * 127.0), 1, 127);
                        var zones = preset.Sf2Preset?.Match(noteNumber, vel127) ?? new List<Sf2Zone>();
                        return new SampleVoice(zones, sampleRate);
                    }
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
                    // krok23: noteNumber se předává kvůli AmplitudeEnvelope (interpolace síly alikvot podle hrané noty).
                    return new OrganVoice(preset, noteNumber, sampleRate);
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
