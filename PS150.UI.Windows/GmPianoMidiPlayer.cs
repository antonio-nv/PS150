using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using PS150.Core.Sampler;   // krok18: Sf2MidiSynth, IMidiOut

namespace PS150.UI.Windows
{
    /// <summary>
    /// Přehrává .mid soubory na PC přes vestavěný Windows syntezátor
    /// "Microsoft GS Wavetable Synth" - natvrdo jako klavír (Acoustic Grand
    /// Piano) na všech 16 kanálech, bez ohledu na to, jaký nástroj je uvedený
    /// přímo v souboru (Program Change eventy ze souboru se ignorují).
    ///
    /// Kanál č. 10 (index 9) je podle standardu General MIDI vždy bicí -
    /// to zajišťuje sám syntezátor (Microsoft GS Wavetable Synth), ne tento
    /// kód. I když mu pošleme "Program Change = klavír" stejně jako všem
    /// ostatním kanálům, na kanálu 10 to interpretuje jako výběr bicí
    /// soupravy, ne nástroje - proto rytmika hraje správně bicími zvuky bez
    /// jakéhokoliv filtrování z naší strany.
    ///
    /// DŮLEŽITÉ: Tahle třída je záměrně úplně oddělená od PS150.Core
    /// (ToneEngine / InputManager / AudioEngine). Core zůstává nedotčený a dál
    /// slouží výhradně pro živé hraní z MIDI-IN klávesnice (varhany, zvony),
    /// s výhledem na budoucí HW nástroj na Raspberry Pi. Přehrávání .mid
    /// souborů na PC je čistě záležitost UI.Windows.
    /// Piano (0–7): Acoustic Grand Piano, Bright Acoustic Piano, Electric Grand Piano, Honky-tonk Piano, Electric Piano 1, Electric Piano 2, Harpsichord, Clavinet
    /// Chromatic Percussion (8–15): Celesta, Glockenspiel, Music Box, Vibraphone, Marimba, Xylophone, Tubular Bells, Dulcimer
    /// Organ (16–23): Drawbar Organ, Percussive Organ, Rock Organ, Church Organ, Reed Organ, Accordion, Harmonica, Tango Accordion
    /// Guitar (24–31): Acoustic Guitar (nylon), Acoustic Guitar (steel), Electric Guitar (jazz), Electric Guitar (clean), Electric Guitar (muted), Overdriven Guitar, Distortion Guitar, Guitar harmonics
    /// Bass (32–39): Acoustic Bass, Electric Bass (finger), Electric Bass (pick), Fretless Bass, Slap Bass 1, Slap Bass 2, Synth Bass 1, Synth Bass 2
    /// Strings (40–47): Violin, Viola, Cello, Double Bass, Tremolo Strings, Pizzicato Strings, Orchestral Harp, Timpani
    /// Ensemble (48–55): String Ensemble 1, String Ensemble 2, Synth Strings 1, Synth Strings 2, Choir Aahs, Voice Oohs, Synth Voice, Orchestra Hit
    /// Brass (56–63): Trumpet, Trombone, Tuba, Muted Trumpet, French Horn, Brass Section, Synth Brass 1, Synth Brass 2
    /// Reed (64–71): Soprano Sax, Alto Sax, Tenor Sax, Baritone Sax, Oboe, English Horn, Bassoon, Clarinet
    /// Pipe (72–79): Piccolo, Flute, Recorder, Pan Flute, Blown Bottle, Shakuhachi, Whistle, Ocarina
    /// Synth Lead (80–87): Lead 1 (square), Lead 2 (sawtooth), Lead 3 (calliope), Lead 4 (chiff), Lead 5 (charang), Lead 6 (voice), Lead 7 (fifths), Lead 8 (bass + lead)
    /// Synth Pad (88–95): Pad 1 (new age), Pad 2 (warm), Pad 3 (polysynth), Pad 4 (choir), Pad 5 (bowed), Pad 6 (metallic), Pad 7 (halo), Pad 8 (sweep)
    /// Synth Effects (96–103): FX 1 (rain), FX 2 (soundtrack), FX 3 (crystal), FX 4 (atmosphere), FX 5 (brightness), FX 6 (goblins), FX 7 (echoes), FX 8 (sci-fi)
    /// Ethnic (104–111): Sitar, Banjo, Shamisen, Koto, Kalimba, Bagpipe, Fiddle, Shanai
    /// Percussive (112–119): Tinkle Bell, Agogo, Steel Drums, Woodblock, Taiko Drum, Melodic Tom, Synth Drum, Reverse Cymbal
    /// Sound Effects (120–127): Guitar Fret Noise, Breath Noise, Seashore, Bird Tweet, Telephone Ring, Helicopter, Applause, Gunshot
    /// 
    /// </summary>
    public class GmPianoMidiPlayer : IDisposable
    {
        private const string DeviceName = "Microsoft GS Wavetable Synth";

        // Číslo nástroje se posílá po drátě jako hodnota 0-127 (Program Change).
        // Tištěné tabulky GM nástrojů bývají číslované 1-128 pro lidi ("č. 1 =
        // Acoustic Grand Piano"), ale fyzicky se posílá 0 - takže hodnota 0 je
        // správně a NEMĚNIT na 1 (to by poslalo "Bright Acoustic Piano", tedy
        // druhý nástroj v tabulce, místo Acoustic Grand Piano).
        private const int AcousticGrandPiano = 0;

        private const int PanControlNumber = 10;   // GM/MIDI Control Change č. 10 = Pan
        // Kanál č. 10 podle GM standardu je index 9 (kanály se číslují od 0).
        private const int PercussionChannel = 9;
        private bool _drumsEnabled = true;

        // krok28: čísla bank použitelných jako CELÝ engine pro přehrávání .mid
        // souborů (musí v sobě mít klavír B000:P000 + bicí B128:P000, jinak
        // Sf2MidiSynth.TryCreate vrátí null a použije se GS Wavetable záloha
        // níž). Úmyslně oddělené od PS150.Core.Banks.BankTable (to je číselník
        // rejstříků živých varhan, ne přehrávacích enginů) - až přibude další
        // skutečně funkční engine (např. OPL/SoundBlaster), stačí ho sem přidat.
        // krok33: přidána _002_GeneralMIDI (stará dobrá GM banka) - dřív tu
        // byla jen MuseScore_General, takže se na GM nedalo přepnout (ani
        // šipkami/kolečkem - StepInList neměla kam skočit - ani ručně zadaným
        // číslem natrvalo: zůstalo by to jen do příštího startu aplikace, pak
        // by to tahle kontrola v FileBrowserWindow tiše vrátila zpátky
        // na první dostupnou banku).
        // krok35: přidána _008_SoundBlaster (OPL3 emulace přes .op2/GENMIDI banku,
        // viz OplMidiSynth/OplGenMidiPath níž) - třetí možnost vedle MuseScore/GM.
        // krok41: přidána _001_SyntetickeZvuky - vlastní syntéza (ToneEngine),
        // viz SyntheticMidiSynth. Na rozdíl od ostatních tří bank výš je tahle
        // jen TÓNOVÁ (žádné "univerzální GM" bicí uvnitř), proto je jen tady,
        // ne v AvailableDrumBanks níž.
        public static readonly int[] AvailablePlaybackBanks =
        {
            PS150.Core.Banks._003_MuseScore_General.Number,
            PS150.Core.Banks._002_GeneralMIDI.Number,
            PS150.Core.Banks._008_SoundBlaster.Number,
            PS150.Core.Banks._001_SyntetickeZvuky.Number,
        };

        // krok30: totéž, ale pro kanál D10 (bicí) ZVLÁŠŤ - viz DualEngineMidiOut.
        // krok33/35: stejné tři banky jako výš. krok41: + _009_SynteticBici -
        // vlastní bicí syntéza (ToneEngine.DrumRegister), jen BICÍ (žádné tóny),
        // proto je jen tady, ne v AvailablePlaybackBanks výš.
        public static readonly int[] AvailableDrumBanks =
        {
            PS150.Core.Banks._003_MuseScore_General.Number,
            PS150.Core.Banks._002_GeneralMIDI.Number,
            PS150.Core.Banks._008_SoundBlaster.Number,
            PS150.Core.Banks._009_SynteticBici.Number,
        };

        private int _playbackBankNumber = PS150.Core.Banks._003_MuseScore_General.Number;
        private int _drumBankNumber = PS150.Core.Banks._003_MuseScore_General.Number;

        // krok36: cesta k .op2 bance pro _008_SoundBlaster - natvrdo vedle
        // PS150.exe, podadresář OP2\GENMIDI.op2 (viz screenshot uživatele -
        // složka OP2 už tam je). Žádná položka v settings.json - uživatel
        // hlásil, že ruční zápis do settings.json se při startu aplikace
        // sám smazal (AppSettings o tom poli nevěděla, dokud nebyl projekt
        // přeložený s touhle verzí - dokud se nepřeloží, serializace
        // neznámou položku zahodí). Natvrdo odvozená cesta tenhle problém
        // úplně obchází.
        private static readonly string OplGenMidiPath =
            System.IO.Path.Combine(AppContext.BaseDirectory, "OP2", "GENMIDI.op2");

        /// <summary>krok28: banka (engine) pro netbicí kanály .mid souborů - nezávislé na bankách živého hraní. Projeví se od příštího PlayAsync.</summary>
        public void SetMidiPlaybackBank(int bankNumber) => _playbackBankNumber = bankNumber;

        /// <summary>krok30: banka (engine) jen pro kanál D10 (bicí) .mid souborů - nezávislé na _playbackBankNumber i na bankách živého hraní. Projeví se od příštího PlayAsync.</summary>
        public void SetMidiDrumBank(int bankNumber) => _drumBankNumber = bankNumber;

        /// <summary>
        /// krok35/36/41: vytvoří engine podle čísla banky - Sf2MidiSynth (SF2
        /// banky), OplMidiSynth (_008_SoundBlaster, cesta k bance viz
        /// OplGenMidiPath výš), nebo SyntheticMidiSynth (_001_SyntetickeZvuky /
        /// _009_SynteticBici - vlastní syntéza, viz ToneEngine). Null, když se
        /// nepodaří (soubor chybí/banka se nenačetla) - volající pak zkusí GS
        /// Wavetable záložku.
        /// krok37: drumRegister se předává jen Sf2MidiSynth (OplMidiSynth bicí
        /// sady nezná - jedna univerzální mapa, viz Op2Bank; SyntheticMidiSynth
        /// taky ne zatím - viz komentář u té třídy).
        /// </summary>
        private IMidiOut? CreateBankEngine(int bankNumber, bool isDrumEngine, int melodicRegister, int drumRegister)
        {
            if (bankNumber == PS150.Core.Banks._008_SoundBlaster.Number)
            {
                if (!System.IO.File.Exists(OplGenMidiPath)) return null;
                return OplMidiSynth.TryCreate(OplGenMidiPath, isDrumEngine, melodicRegister);
            }
            if (bankNumber == PS150.Core.Banks._001_SyntetickeZvuky.Number
                || bankNumber == PS150.Core.Banks._009_SynteticBici.Number)
            {
                return SyntheticMidiSynth.Create(isDrumEngine);
            }
            return Sf2MidiSynth.TryCreate(bankNumber, melodicRegister, drumRegister);
        }

        /// <summary>
        /// krok43: obal nad CreateBankEngine, co navíc pochytá VÝJIMKU (ne jen
        /// null) a vrátí ji ven jako text - ať je u fallbacku na GS Wavetable
        /// vidět PROČ k němu došlo (např. "device already allocated" u
        /// WaveOutEvent, když se předchozí engine ještě nestihl doopravdy
        /// uvolnit), ne jen tichý skok na zálohu beze stopy.
        /// </summary>
        private IMidiOut? TryCreateBankEngine(int bankNumber, bool isDrumEngine, int melodicRegister, int drumRegister, out string? error)
        {
            try
            {
                error = null;
                return CreateBankEngine(bankNumber, isDrumEngine, melodicRegister, drumRegister);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // krok31: univerzální tónový rejstřík - 1-128 = vynutí tenhle GM
        // program na VŠECH netbicích kanálech (1 = Acoustic Grand Piano,
        // odpovídá starému natvrdo zapsanému chování). 0 = žádný univerzální
        // rejstřík - hrají skutečné nástroje podle Program Change v souboru.
        private int _melodicRegister = 1;

        // krok37: totéž, ale pro bicí sadu (kit) na kanálu D10 - "lidské" GM/GS
        // číslování 1-128 (1 = Standard kit, odpovídá starému natvrdo zapsanému
        // chování P000). 0 = žádný vynucený rejstřík - hraje skutečná sada
        // podle Program Change na kanálu D10 v souboru.
        private int _drumRegister = 1;

        /// <summary>
        /// krok31: viz komentář u _melodicRegister výš. Projeví se od příštího PlayAsync.
        /// krok44: u syntetické banky (_001_SyntetickeZvuky) tahle hodnota NEZNAMENÁ
        /// GM program 0-127, ale PŘÍMO číslo rejstříku z ToneEngine (200, 300,
        /// 600...) - viz ISupportsSyntheticRegister/SyntheticMidiSynth.
        /// krok45: u syntetiky se zadané číslo napřed "přichytí" na nejbližší
        /// SKUTEČNĚ definovaný rejstřík (viz ToneEngine.NearestDefinedRegisterNumber)
        /// - jinak by v poli klidně zůstalo třeba 150, které v BuildRegistry()
        /// vůbec neexistuje (mezi 108 a 200), a nic by se neznělo. Vrací
        /// efektivně použité číslo, ať ho volající (UniR pole) může zobrazit
        /// místo toho, co se doopravdy zadalo.
        /// </summary>
        public int SetMidiMelodicRegister(int gmProgram)
        {
            bool tonalIsSynthetic = _playbackBankNumber == PS150.Core.Banks._001_SyntetickeZvuky.Number;

            int effective = gmProgram;
            if (tonalIsSynthetic && gmProgram > 0)
            {
                effective = PS150.Core.ToneEngine.ToneEngine.NearestDefinedRegisterNumber(_playbackBankNumber, gmProgram)
                    ?? gmProgram;
            }

            _melodicRegister = effective;

            // krok34/44/45: ZAŽIVA přepne i právě hrající soubor, pokud to aktuální
            // engine umí (Sf2MidiSynth nebo SyntheticMidiSynth přes DualEngineMidiOut)
            // - bez přerušení přehrávání. GS Wavetable záloha to neumí, tam se jen
            // uloží hodnota pro příští PlayAsync (viz _melodicRegister výš).
            // krok45: DualEngineMidiOut implementuje OBĚ rozhraní VŽDY (ať je
            // tónový subengine jakýkoliv) a jen tiše no-opne to, co subengine
            // neumí - proto se tu rozhoduje podle _playbackBankNumber (co
            // doopravdy hraje), ne podle "is ISupportsMelodicRegister", které by
            // bylo pravda pořád a syntetiku by to nikdy nedostalo ke slovu.
            if (tonalIsSynthetic)
                (_outputDevice as ISupportsSyntheticRegister)?.SetSyntheticRegister(effective);
            else
                (_outputDevice as ISupportsMelodicRegister)?.SetUniversalProgram(effective);

            return effective;
        }

        /// <summary>krok37: totéž, ale pro bicí sadu (kit), viz komentář u _drumRegister výš. Projeví se od příštího PlayAsync.</summary>
        public void SetMidiDrumRegister(int gmProgram)
        {
            _drumRegister = gmProgram;

            // krok37: stejná logika jako SetMidiMelodicRegister výš - ZAŽIVA
            // přepne i právě hrající soubor, pokud to engine umí.
            if (_outputDevice is ISupportsDrumRegister supported) supported.SetDrumProgram(gmProgram);
        }

        /// <summary>True = rytmika (kanál D10) hraje normálně, false = je umlčená - viz TogglePercussion().</summary>
        public bool DrumsEnabled => _drumsEnabled;

        /// <summary>Zapne/vypne kanál D10 (rytmika) - klávesa D/d ve FileBrowserWindow. Při vypnutí umlčí i právě znějící bicí, ať nezůstane "viset" nota.</summary>
        public void SetDrumsEnabled(bool enabled)
        {
            _drumsEnabled = enabled;

            if (!enabled && _outputDevice != null)
            {
                // All Sound Off (CC 120) na kanálu D10 - umlčí okamžitě
                // cokoliv, co už hraje, ne jen budoucí noty.
                _outputDevice.SendEvent(new ControlChangeEvent((SevenBitNumber)120, (SevenBitNumber)0)
                {
                    Channel = (FourBitNumber)PercussionChannel
                });
            }
        }

        private const int VolumeControlNumber = 7;  // GM/MIDI Control Change č. 7 = Channel Volume

        // Výchozí rozsah pitch bendu podle General MIDI (±2 půltóny = ±200
        // centů na plný rozsah ±8192). Soubory se čtvrttóny tenhle výchozí
        // rozsah nemění (žádný RPN 0,0 na Pitch Bend Sensitivity), takže
        // 50 centů (čtvrttón) = 2048, 100 centů (půltón) = 4096, 150 centů
        // (tříčtvrttón) = 6144 z plného rozsahu ±8192.
        private const double PitchBendRangeCents = 200.0;
        private readonly double[] _channelBendCents = new double[16];

        // ------------------------------------------------------------------
        // NOVÉ: ladění / transpozice (klávesa t ve FileBrowserWindow).
        //
        // Jak se ladění dělá u MIDI přes Windows syntezátor (ten umí ladit
        // jedině přes pitch bend celého kanálu, ne po jednotlivých notách):
        //  - CELÉ půltóny (PlayerTuning.WholeSemitones) posunou číslo noty
        //    ve zprávách NoteOn/NoteOff,
        //  - zbytek v centech (PlayerTuning.RemainderCents, vždy -50..+50)
        //    se přičte k pitch bendu kanálu. Bend ze souboru (např. čtvrttóny
        //    ve skladbě) se tím nepřepíše, jen se k němu přičítá.
        //  - bicí (kanál D10) se neladí vůbec - bicí nástroje nemají výšku.
        //
        // PlayerTuning je z PS150.Core, ale je to jen malá neutrální
        // pomůcka s čísly - žádný syntezátor ani ToneEngine, takže původní
        // oddělení od Core (viz komentář třídy výše) zůstává zachované.
        //
        // PlayerTuning je neměnná (immutable) třída, proto stačí "volatile"
        // odkaz: UI vlákno vymění celý objekt, přehrávací vlákno si vždy
        // přečte jeden konzistentní.
        // ------------------------------------------------------------------
        private volatile PS150.Core.PlayerTuning _tuning = PS150.Core.PlayerTuning.Off;

        // (kanál, ČÍSLO NOTY ZE SOUBORU) -> číslo noty, které jsme skutečně
        // poslali syntezátoru. Je to nutné, protože ladění se může změnit
        // zrovna, když nota ještě zní: NoteOff pak musí jít na TU SAMOU
        // (posunutou) notu jako NoteOn, jinak by nota zůstala viset.
        // Používá se jen z přehrávacího vlákna.
        private readonly Dictionary<(int Channel, int Note), int> _sentNotes = new();

        // ------------------------------------------------------------------
        // NOVÉ: AJI (adaptivní čisté ladění, klávesa a ve FileBrowserWindow).
        //
        // Při načtení souboru se předem spočítá plán (PS150.Core.AjiPlanner):
        // tónina skladby a pro každou notu kanál + pitch bend, aby zněla
        // v čistém ladění vůči tónice. Přehrávač pak jen podle vypínače
        // zahraje buď původní noty (temperovaný klavír, jako dřív), nebo
        // noty z plánu. Přepnutí je okamžité, nota, která už zní, doznívá
        // po svém.
        //
        // Pozor: pedál (CC64) se při zapnutém AJI nepředává syntezátoru,
        // ale emuluje se tady (_pedalDown / _pedalHeld) - noty z jednoho
        // kanálu souboru se totiž rozdělují na víc skutečných kanálů, takže
        // pedál syntezátoru by držel špatné noty. Ostatní kontrolery se
        // předávají beze změny (volume, pan a program se řídí jinde, viz výše).
        // ------------------------------------------------------------------
        private volatile bool _ajiEnabled = false;
        private volatile PS150.Core.AjiPlan? _ajiPlan;
        private readonly object _ajiLock = new object();

        // Právě znějící noty zahrané přes AJI: (kanál ze souboru, nota ze souboru) -> (skutečný kanál, skutečná nota).
        private readonly Dictionary<(int OrigChannel, int OrigNote), (int PoolChannel, int SentNote)> _ajiActive = new();

        // Pedál podle kanálů ze souboru + noty, které pedál drží i po NoteOff.
        private readonly bool[] _pedalDown = new bool[16];
        private readonly List<(int OrigChannel, int OrigNote)>[] _pedalHeld = CreateHeldLists();

        // Naposledy poslané nastavení skutečných kanálů (aby se zbytečně neposílalo znovu). -1 / NaN = neznámé.
        private readonly int[] _poolPan = Enumerable.Repeat(-1, 16).ToArray();
        private readonly double[] _poolBend = Enumerable.Repeat(double.NaN, 16).ToArray();

        // Pitch bend přímo ze souboru (bez ladění a AJI) - pro zobrazení not a pro návrat z AJI.
        private readonly double[] _fileBendCents = new double[16];

        private static List<(int OrigChannel, int OrigNote)>[] CreateHeldLists()
        {
            var lists = new List<(int OrigChannel, int OrigNote)>[16];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<(int OrigChannel, int OrigNote)>();
            return lists;
        }

        /// <summary>True, pokud je AJI zapnuté.</summary>
        public bool AjiEnabled => _ajiEnabled;

        /// <summary>True, pokud se pro aktuální soubor podařilo spočítat plán AJI.</summary>
        public bool AjiAvailable => _ajiPlan != null;

        /// <summary>Text pro obrazovku: tónina platná v aktuálním čase přehrávání (např. "Fis dur").</summary>
        public string AjiStatusText
        {
            get
            {
                var plan = _ajiPlan;
                if (plan == null) return "bez analýzy";
                return plan.DescribeKeyAt(CurrentTime.TotalSeconds);
            }
        }

        /// <summary>
        /// Zapne / vypne AJI za chodu. Noty, které už znějí, nechá doznít;
        /// nové noty se zahrají podle nového nastavení.
        /// </summary>
        public void SetAjiEnabled(bool enabled)
        {
            lock (_ajiLock)
            {
                if (_ajiEnabled == enabled) return;
                _ajiEnabled = enabled;

                // Naposledy poslané nastavení skutečných kanálů už nemusí platit.
                Array.Fill(_poolPan, -1);
                Array.Fill(_poolBend, double.NaN);

                if (enabled)
                {
                    // Od teď pedál emulujeme sami - syntezátoru ho proto nejdřív uvolníme na všech
                    // kanálech, aby mu žádná dřívější noha "nezůstala dole".
                    for (int channel = 0; channel <= 15; channel++)
                    {
                        if (channel != PercussionChannel) SendControl(channel, 64, 0);
                    }
                }
                else
                {
                    // Noty držené naším pedálem pustíme a vrátíme kanálům pitch bend ze souboru.
                    for (int channel = 0; channel <= 15; channel++)
                    {
                        ReleasePedalHeld(channel);
                    }

                    for (int channel = 0; channel <= 15; channel++)
                    {
                        _channelBendCents[channel] = _fileBendCents[channel];
                    }

                    ReassertChannelState();

                    // Pedál podle skutečného stavu v souboru zase předáme syntezátoru.
                    for (int channel = 0; channel <= 15; channel++)
                    {
                        if (channel != PercussionChannel && _pedalDown[channel]) SendControl(channel, 64, 127);
                    }
                }
            }
        }

        /// <summary>Vynuluje veškerý stav AJI (nový soubor). Plán samotný se nastavuje zvlášť.</summary>
        private void ResetAjiState()
        {
            lock (_ajiLock)
            {
                _ajiActive.Clear();
                Array.Clear(_pedalDown, 0, _pedalDown.Length);
                foreach (var list in _pedalHeld) list.Clear();
                Array.Fill(_poolPan, -1);
                Array.Fill(_poolBend, double.NaN);
                Array.Clear(_fileBendCents, 0, _fileBendCents.Length);
            }
        }

        /// <summary>
        /// Spočítá plán AJI pro právě načtený soubor (jednou, hned po načtení).
        /// Při jakékoliv chybě plán prostě nebude (AJI se pak chová jako
        /// vypnuté) - přehrávání samotné se kvůli tomu nikdy nesmí zastavit.
        /// </summary>
        private void BuildAjiPlan(MidiFile midiFile, int[] usedChannels)
        {
            _ajiPlan = null;

            try
            {
                var tempoMap = midiFile.GetTempoMap();

                var noteInputs = new List<PS150.Core.AjiNoteInput>();
                foreach (Note note in midiFile.GetNotes())
                {
                    double start = ((TimeSpan)note.TimeAs<MetricTimeSpan>(tempoMap)).TotalSeconds;
                    double end = ((TimeSpan)note.EndTimeAs<MetricTimeSpan>(tempoMap)).TotalSeconds;
                    noteInputs.Add(new PS150.Core.AjiNoteInput(note.Channel, note.NoteNumber, start, end));
                }

                var pedalInputs = new List<PS150.Core.AjiPedalInput>();
                foreach (TimedEvent timedEvent in midiFile.GetTimedEvents())
                {
                    if (timedEvent.Event is ControlChangeEvent cc && cc.ControlNumber == 64)
                    {
                        double time = ((TimeSpan)timedEvent.TimeAs<MetricTimeSpan>(tempoMap)).TotalSeconds;
                        pedalInputs.Add(new PS150.Core.AjiPedalInput(cc.Channel, time, cc.ControlValue >= 64));
                    }
                }

                // Panorámu zdědí každá nota z kanálu souboru - stejné ping-pong pravidlo jako v ApplyPanLayout.
                int[] panByChannel = Enumerable.Repeat(PanCenter, 16).ToArray();
                if (usedChannels.Length > 1)
                {
                    for (int i = 0; i < usedChannels.Length; i++)
                    {
                        panByChannel[usedChannels[i]] = (i % 2 == 0) ? PanLeft : PanRight;
                    }
                }

                _ajiPlan = PS150.Core.AjiPlanner.Build(noteInputs, pedalInputs, panByChannel);
            }
            catch (Exception ex)
            {
                _ajiPlan = null;
                System.Diagnostics.Debug.WriteLine($"[GmPianoMidiPlayer] AJI plán se nepodařilo spočítat: {ex.Message}");
            }
        }

        /// <summary>
        /// Zahraje notu podle plánu AJI. Vrací false, pokud se AJI nepoužije
        /// (vypnuto, bicí, žádný plán, nota v plánu není) - přehrávač pak
        /// zahraje notu normálně (temperovaně).
        /// </summary>
        private bool AjiHandleNoteOn(NoteOnEvent noteOn)
        {
            if (!_ajiEnabled) return false;

            var plan = _ajiPlan;
            var device = _outputDevice;
            int channel = noteOn.Channel;
            int note = noteOn.NoteNumber;

            if (plan == null || device == null || channel == PercussionChannel || noteOn.Velocity == 0) return false;

            if (!plan.TryGetNote(channel, note, CurrentTime.TotalSeconds, PS150.Core.AjiPlanner.MatchToleranceSeconds, out var notePlan)
                || notePlan == null)
            {
                return false;
            }

            lock (_ajiLock)
            {
                // Stejná nota se znovu rozeznívá, než předchozí skončila - tu starou nejdřív vypneme.
                if (_ajiActive.TryGetValue((channel, note), out var previous))
                {
                    SendNoteOffRaw(device, previous.PoolChannel, previous.SentNote, 0);
                    _ajiActive.Remove((channel, note));
                }

                int pool = notePlan.PoolChannel;

                if (_poolPan[pool] != notePlan.Pan)
                {
                    SendControl(pool, PanControlNumber, notePlan.Pan);
                    _poolPan[pool] = notePlan.Pan;
                }

                if (double.IsNaN(_poolBend[pool]) || Math.Abs(_poolBend[pool] - notePlan.OffsetCents) > 0.001)
                {
                    _poolBend[pool] = notePlan.OffsetCents;
                    _channelBendCents[pool] = notePlan.OffsetCents;
                    SendBend(pool);
                }

                int sentNote = note + _tuning.WholeSemitones;
                if (sentNote < 0 || sentNote > 127) return true;   // mimo rozsah - nezahraje se

                device.SendEvent(new NoteOnEvent((SevenBitNumber)sentNote, noteOn.Velocity)
                {
                    Channel = (FourBitNumber)pool
                });

                _ajiActive[(channel, note)] = (pool, sentNote);
            }

            return true;
        }

        /// <summary>
        /// Vypne notu zahranou přes AJI (a respektuje emulovaný pedál).
        /// Vrací false, pokud nota přes AJI nezněla - pak ji vypne běžná cesta.
        /// </summary>
        private bool AjiHandleNoteOff(NoteOffEvent noteOff)
        {
            int channel = noteOff.Channel;
            int note = noteOff.NoteNumber;

            lock (_ajiLock)
            {
                if (!_ajiActive.ContainsKey((channel, note))) return false;

                if (_ajiEnabled && _pedalDown[channel])
                {
                    _pedalHeld[channel].Add((channel, note));
                    return true;
                }

                AjiSendNoteOff(channel, note, noteOff.Velocity);
            }

            return true;
        }

        private void AjiSendNoteOff(int origChannel, int origNote, int velocity)
        {
            if (!_ajiActive.TryGetValue((origChannel, origNote), out var active)) return;
            _ajiActive.Remove((origChannel, origNote));

            var device = _outputDevice;
            if (device != null) SendNoteOffRaw(device, active.PoolChannel, active.SentNote, velocity);
        }

        private static void SendNoteOffRaw(IMidiOut device, int channel, int note, int velocity)
        {
            device.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)velocity)
            {
                Channel = (FourBitNumber)channel
            });
        }

        /// <summary>Pustí noty, které držel emulovaný pedál daného kanálu.</summary>
        private void ReleasePedalHeld(int channel)
        {
            var held = _pedalHeld[channel];
            if (held.Count == 0) return;

            foreach (var (origChannel, origNote) in held.ToArray())
            {
                AjiSendNoteOff(origChannel, origNote, 0);
            }
            held.Clear();
        }

        /// <summary>
        /// Pedál (CC64) ze souboru. Stav se sleduje vždy; při vypnutém AJI se
        /// zároveň předá syntezátoru (jako dřív), při zapnutém ho emulujeme.
        /// </summary>
        private void HandlePedal(ControlChangeEvent pedal)
        {
            int channel = pedal.Channel;
            bool down = pedal.ControlValue >= 64;

            lock (_ajiLock)
            {
                _pedalDown[channel] = down;

                if (!_ajiEnabled)
                {
                    _outputDevice?.SendEvent(pedal);
                    return;
                }

                if (!down) ReleasePedalHeld(channel);
            }
        }

        /// <summary>
        /// Nastaví ladění a hned ho promítne do pitch bendu všech kanálů.
        /// Noty, které už znějí, si nechají původní číslo noty (jen se jim
        /// plynule změní jemné doladění), nové noty už vzniknou s novým
        /// laděním. Lze volat kdykoliv, i když nic nehraje.
        /// </summary>
        public void SetTuning(PS150.Core.PlayerTuning tuning)
        {
            _tuning = tuning ?? PS150.Core.PlayerTuning.Off;
            SendTuningBendToAllChannels();
        }

        /// <summary>
        /// True, pokud tónina souboru (viz KeySignatureEvent) preferuje
        /// béčka (F dur, B dur, Es dur...) - používá se při zobrazování
        /// názvů not, aby se černé klávesy neukazovaly automaticky jako
        /// křížky, když skladba je zjevně v béčkové tónině.
        /// </summary>
        public bool KeyPrefersFlats { get; private set; }

        /// <summary>
        /// krok46: VU metr pro .mid přehrávání - průchoz na aktuální
        /// _outputDevice (Sf2MidiSynth/OplMidiSynth/SyntheticMidiSynth přes
        /// DualEngineMidiOut, viz IProvidesAudioMeter). Záložní Windows
        /// syntezátor (GsWavetableOut) tohle neumí - vrátí tiše (0,0)/false,
        /// stejně jako dřív nebylo co měřit.
        /// </summary>
        public (float left, float right) ReadPeakLevels() =>
            (_outputDevice as PS150.Core.Sampler.IProvidesAudioMeter)?.ReadPeakLevels() ?? (0f, 0f);

        /// <summary>krok46: viz ReadPeakLevels výš.</summary>
        public bool ClipDetected => (_outputDevice as PS150.Core.Sampler.IProvidesAudioMeter)?.ClipDetected ?? false;

        private const int PanLeft = 0;
        private const int PanCenter = 64;
        private const int PanRight = 127;

        private const int VolumeLsbControlNumber = 39;       // CC39 = Channel Volume (jemná část)
        private const int ResetAllControllersNumber = 121;   // CC121 = Reset All Controllers

        // Kanály použité v právě přehrávaném souboru - pro obnovu ping-pong
        // panorámy po resetu ze souboru (viz ReassertChannelState).
        private volatile int[] _usedChannelsForPan = Array.Empty<int>();

        // krok18: výstup přehrávače .mid. Natvrdo vlastní sampler (klavír + bicí z banky MuseScore_General,
        // viz Sf2MidiSynth); když banka ještě není načtená, zůstává záloha Microsoft GS Wavetable Synth.
        private IMidiOut? _outputDevice;

        /// <summary>krok20: popis aktuálního zdroje zvuku pro obrazovku (zůstává i po doznění skladby).</summary>
        public string OutputDescription { get; private set; } = "MIDI";

        /// <summary>
        /// krok18: obal Windows syntezátoru jako IMidiOut (záloha, dokud se banka nenačte).
        /// krok64: eventy jdou přes SharedGmOutput (JEDNO sdílené spojení pro
        /// celou appku, viz tamní komentář - dřív si tenhle přehrávač otvíral
        /// vlastní OutputDevice, což při souběhu s živým hraním na bance 002
        /// shazovalo appku). Dispose() je proto záměrně prázdný - sdílené
        /// zařízení nepatří jen tomuhle přehrávání, nezavírá se.
        /// </summary>
        private sealed class GsWavetableOut : IMidiOut
        {
            public string Description => "Microsoft GS Wavetable Synth (záloha)";
            public void SendEvent(MidiEvent midiEvent) => SharedGmOutput.SendEvent(midiEvent);
            public void Dispose() { }
        }
        private CancellationTokenSource? _cts;
        private Task? _playbackTask;
        private Playback? _playback;

        // Hlavní hlasitost (0-100) ovládaná šipkami nahoru/dolů ve VGA konzoli.
        // Násobí se s hlasitostí, kterou si případně řídí sám soubor (CC7) -
        // viz EventPlayed níž. Výchozí 100 = beze změny.
        private volatile int _masterVolumePercent = 100;

        // Nastaveno v Pause() / zrušeno v Resume() - viz komentář u monitorovací
        // smyčky v PlayAsync níže. Bez tohohle příznaku by playback.Stop() volané
        // z Pause() smyčku ukončilo úplně stejně, jako kdyby skladba doopravdy
        // dohrála - a VgaEngine by omylem skočila na další soubor.
        private volatile bool _isPausedByUser = false;

        /// <summary>True, pokud je přehrávání aktuálně pozastavené přes Pause().</summary>
        public bool IsPaused => _isPausedByUser;

        // Celková délka aktuálně načteného souboru - spočítaná JEDNOU při
        // načtení (viz PlayAsync), ne odhadovaná. Na rozdíl od komprimovaného
        // zvuku (MP3 apod.) je délka MIDI souboru známá přesně už ze
        // samotných dat (delta-časy + tempo-eventy), žádné dopočítávání
        // z velikosti souboru nebo bitrate.
        private TimeSpan _totalTime = TimeSpan.Zero;
        public TimeSpan TotalTime => _totalTime;

        /// <summary>
        /// Aktuální pozice přehrávání. Čte se přímo z DryWetMidi Playbacku,
        /// takže odráží i posun po Seek() nebo po Pause()/Resume().
        /// </summary>
        public TimeSpan CurrentTime
        {
            get
            {
                var playback = _playback;
                if (playback == null) return TimeSpan.Zero;
                return (TimeSpan)playback.GetCurrentTime<MetricTimeSpan>();
            }
        }

        /// <summary>Vyvoláno při rozeznění noty. Parametry: (kanál 0-15, MIDI číslo noty).</summary>
        public event Action<int, int, double>? NoteOnRaised;

        /// <summary>Vyvoláno při doznění noty. Parametry: (kanál 0-15, MIDI číslo noty).</summary>
        public event Action<int, int>? NoteOffRaised;

        /// <summary>
        /// Vyvoláno jednou po načtení souboru se seznamem kanálů (notových
        /// osnov), které soubor používá, seřazeným vzestupně.
        /// </summary>
        public event Action<int[]>? ChannelsDetected;

        /// <summary>
        /// Vyvoláno, když skladba dohraje sama do konce (NE když ji přeruší
        /// Stop() kvůli přechodu na jiný soubor nebo ukončení aplikace).
        /// </summary>
        public event Action? PlaybackFinishedNaturally;

        /// <summary>
        /// Vyvoláno, když se soubor nepodaří načíst/přehrát (poškozený nebo
        /// nestandardní .mid soubor). Po tomto eventu se ZÁMĚRNĚ
        /// NEpokračuje na další soubor - zůstáváme stát na tom problémovém,
        /// ať se dá podle chybové zprávy diagnostikovat.
        /// </summary>
        public event Action<Exception>? PlaybackFailed;

        public async Task PlayAsync(string filePath)
        {
            if (!File.Exists(filePath)) return;

            Stop();
            LastSeekDiagnostic = null;
            _totalTime = TimeSpan.Zero;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            MidiFile midiFile;
            int[] usedChannels;

            try
            {
                // krok30: dva nezávislé enginy - jeden pro kanál D10 (bicí, viz
                // SetMidiDrumBank), jeden pro všechny ostatní kanály (viz
                // SetMidiPlaybackBank) - spojené přes DualEngineMidiOut, který
                // eventy rozesílá podle kanálu. Když se obě banky shodují (dnes
                // vždycky, viz AvailablePlaybackBanks/AvailableDrumBanks), chová
                // se to navenek stejně jako dřív jeden sdílený engine.
                // krok31: univerzální rejstřík se týká jen netbicích kanálů -
                // drumEngine pořád jen bicí (D10), tam na tom nezáleží.
                var tonalEngine = TryCreateBankEngine(_playbackBankNumber, isDrumEngine: false, _melodicRegister, _drumRegister, out var tonalError);
                var drumEngine = TryCreateBankEngine(_drumBankNumber, isDrumEngine: true, melodicRegister: 1, _drumRegister, out var drumError);

                if (tonalEngine != null && drumEngine != null)
                {
                    _outputDevice = new DualEngineMidiOut(tonalEngine, drumEngine);
                }
                else
                {
                    tonalEngine?.Dispose();
                    drumEngine?.Dispose();
                    _outputDevice = null;
                }

                // ... a když ani jedna banka ještě není načtená, záloha: Windows syntezátor jako dřív.
                if (_outputDevice == null)
                {
                    // krok64: SharedGmOutput místo vlastního OutputDevice.GetByName - viz
                    // komentář u GsWavetableOut/SharedGmOutput.
                    var gs = SharedGmOutput.TryGet();
                    if (gs == null)
                    {
                        throw new InvalidOperationException(
                            $"Výstupní zařízení '{DeviceName}' nebylo na tomto PC nalezeno.");
                    }
                    _outputDevice = new GsWavetableOut();
                }

                // krok43: když došlo na zálohu KVŮLI výjimce (ne kvůli běžnému
                // "soubor/banka ještě nejsou k dispozici"), připíšeme k popisku
                // i hlášku té výjimky - ať je vidět proč, místo tichého fallbacku.
                var engineErrors = new List<string>();
                if (tonalError != null) engineErrors.Add($"tón: {tonalError}");
                if (drumError != null) engineErrors.Add($"bicí: {drumError}");
                OutputDescription = engineErrors.Count > 0
                    ? $"{_outputDevice.Description} [{string.Join("; ", engineErrors)}]"
                    : _outputDevice.Description;

                // NOVÉ: tolerantní čtení - některé (hlavně herní) .mid soubory mají
                // v řídicí události hodnotu mimo rozsah 0-127 (např. 128). DryWetMidi
                // by jinak celý soubor odmítl s chybou "128 is invalid value ...";
                // SnapToLimits hodnotu oseká na nejbližší platnou (127), jak to dělají
                // jiné přehrávače.
                //
                // krok51: NotEnoughBytesPolicy.Ignore - některé staré herní .mid
                // soubory (typicky komunitní balíčky typu TeamTNT Musicarus pro
                // DOOM) mají za posledním platným MTrk blokem přilepený kus
                // náhodného balastu navíc (zbytek po extrakci/balení, ne poškozená
                // hudba - samotných deklarovaných 10/N stop bývá úplně v pořádku
                // a správně ukončených). DryWetMidi se po dočtení poslední stopy
                // snaží zkontrolovat, jestli nenásleduje ještě další blok, narazí
                // na ten balast, vyjde mu nesmyslně obří "deklarovaná délka" a
                // skončí chybou "doesn't have enough bytes". Ignore takový
                // přebytečný ocas na konci souboru tiše přeskočí a zbytek (celou
                // skutečnou hudbu) přečte normálně.
                //
                // krok52: InvalidChunkSizePolicy.Ignore - samostatná, NEZÁVISLÁ
                // kontrola od NotEnoughBytesPolicy výš. I když se podaří ten
                // balastní "chunk" nějak dočíst (zbylo na něj dost bajtů), engine
                // navíc porovná, kolik bajtů opravdu přečetl, s tím, co bylo
                // deklarováno v hlavičce - a když to nesedí (u našeho balastu
                // nesedí skoro jistě), skončí druhou, samostatnou chybou
                // "Actual size (...) differs from the one declared...". Ignore
                // tenhle nesoulad taky jen tiše přejde.
                midiFile = MidiFile.Read(filePath, new ReadingSettings
                {
                    InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
                    NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
                    InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore
                });

                // Celková délka - spočítaná přesně z dat souboru (viz komentář
                // u TotalTime výše), jednou hned po načtení.
                _totalTime = (TimeSpan)midiFile.GetDuration<MetricTimeSpan>();

                // Zjistíme, které kanály (notové osnovy) soubor vůbec používá - pro
                // trvalé zobrazení všech osnov ve VGA konzoli a pro ping-pong
                // panorámu níž.
                usedChannels = midiFile.GetTimedEvents()
                    .Select(te => te.Event)
                    .OfType<NoteOnEvent>()
                    .Select(n => (int)n.Channel)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToArray();

                // Tónina souboru - Key < 0 znamená béčkovou tóninu (F dur,
                // B dur, Es dur...). Bereme první KeySignatureEvent v
                // souboru (typicky hned na začátku); pokud soubor žádný
                // nemá, KeyPrefersFlats zůstává false (křížky, jako dřív).
                var keySignature = midiFile.GetTimedEvents()
                    .Select(te => te.Event)
                    .OfType<KeySignatureEvent>()
                    .FirstOrDefault();
                KeyPrefersFlats = keySignature != null && keySignature.Key < 0;

                Array.Clear(_channelBendCents, 0, _channelBendCents.Length);
            }
            catch (Exception ex)
            {
                // Soubor se nepodařilo ani načíst/rozebrat - nahlásíme chybu
                // a NEpokračujeme dál (viz komentář u PlaybackFailed).
                PlaybackFailed?.Invoke(ex);
                return;
            }

            ChannelsDetected?.Invoke(usedChannels);

            // krok34: natvrdo nastavíme univerzální rejstřík na netbicích
            // kanálech ještě PŘED přehráváním - ale JEN když je nějaký vynucený
            // (_melodicRegister != 0). Dřív se tu natvrdo posílal klavír vždycky
            // a Program Change ze souboru se v EventPlayed níž zahazoval úplně -
            // takže "0 = skutečné nástroje ze souboru" (viz SetMidiMelodicRegister)
            // nemohlo nikdy nic udělat, protože klavír byl nastavený tady a
            // skutečné nástroje ze souboru se nikdy nedostaly ani k enginu.
            // krok37: kanál D10 (bicí) vynecháváme - ten má teď VLASTNÍ rejstřík
            // (_drumRegister, viz blok hned pod tímhle).
            // krok44: u syntetické banky (_001_SyntetickeZvuky) _melodicRegister
            // NENÍ GM program 0-127, ale přímo číslo rejstříku z ToneEngine (až
            // 999) - poslat ho jako ProgramChangeEvent by přeteklo SevenBitNumber
            // (0-127) a navíc by ho SyntheticMidiSynth stejně ignoroval (Program
            // Change se u něj nesleduje). Místo toho jde rovnou přes
            // ISupportsSyntheticRegister.
            // krok45: rozhoduje se podle _playbackBankNumber, NE podle
            // "_outputDevice is ISupportsSyntheticRegister" - DualEngineMidiOut
            // tohle rozhraní implementuje VŽDY (jen tiše no-opne, co tónový
            // subengine neumí), takže by ta podmínka byla pravda i pro SF2/OPL
            // a GM vynucování programu by se přestalo posílat úplně.
            bool tonalIsSynthetic = _playbackBankNumber == PS150.Core.Banks._001_SyntetickeZvuky.Number;
            if (_melodicRegister != 0 && tonalIsSynthetic)
            {
                // krok45: i tady se číslo přichytí na nejbližší skutečně
                // definovaný rejstřík - viz komentář u SetMidiMelodicRegister.
                int snapped = PS150.Core.ToneEngine.ToneEngine.NearestDefinedRegisterNumber(_playbackBankNumber, _melodicRegister)
                    ?? _melodicRegister;
                (_outputDevice as ISupportsSyntheticRegister)?.SetSyntheticRegister(snapped);
            }
            else if (_melodicRegister != 0)
            {
                int gmProgram = Math.Clamp(_melodicRegister, 1, 128) - 1;
                for (int channel = 0; channel <= 15; channel++)
                {
                    if (channel == PercussionChannel) continue;
                    _outputDevice!.SendEvent(new ProgramChangeEvent((SevenBitNumber)gmProgram)
                    {
                        Channel = (FourBitNumber)channel
                    });
                }
            }

            // krok37: totéž, ale pro bicí sadu (kit) na kanálu D10 - stejná
            // logika jako výš, jen natvrdo jen na jednom kanálu.
            if (_drumRegister != 0)
            {
                int gmDrumProgram = Math.Clamp(_drumRegister, 1, 128) - 1;
                _outputDevice!.SendEvent(new ProgramChangeEvent((SevenBitNumber)gmDrumProgram)
                {
                    Channel = (FourBitNumber)PercussionChannel
                });
            }

            for (int channel = 0; channel <= 15; channel++)
            {
                // Počáteční hlasitost podle aktuálního nastavení VGA konzole.
                _outputDevice!.SendEvent(new ControlChangeEvent(
                    (SevenBitNumber)VolumeControlNumber, (SevenBitNumber)ScaleToMidiVolume(127))
                {
                    Channel = (FourBitNumber)channel
                });
            }

            // Ping-pong panoráma mezi jednotlivými osnovami: je-li osnova jen
            // jedna, necháme ji na středu (zní stejně silně na obou kanálech).
            // Je-li osnov víc, střídáme je pravidelně vlevo/vpravo podle pořadí
            // čísla kanálu. Pan eventy ze souboru samotného níž v EventPlayed
            // zahazujeme, ať nám tohle rozložení později v playbacku nikdo
            // nepřepíše.
            _usedChannelsForPan = usedChannels;
            ApplyPanLayout();

            // NOVÉ: ladění. Pitch bend se zde počítá z rozsahu ±2 půltóny,
            // proto ho nejdřív výslovně nastavíme na všech kanálech (RPN 0,0),
            // pak zapomeneme noty z předchozího souboru a pošleme každému
            // kanálu výchozí bend včetně jemného doladění z klávesy t.
            SetPitchBendRangeOnAllChannels();
            _sentNotes.Clear();
            SendTuningBendToAllChannels();

            // NOVÉ: AJI. Zapomene stav z předchozího souboru a předem spočítá
            // plán pro tenhle (tónina + rozdělení not na kanály, viz AjiPlanner).
            // Trvá to jen okamžik a při chybě se prostě bez AJI pokračuje.
            ResetAjiState();
            BuildAjiPlan(midiFile, usedChannels);

            _playbackTask = Task.Run(() =>
            {
                try
                {
                    using var playback = new Playback(midiFile.GetTimedEvents(), midiFile.GetTempoMap());
                    _playback = playback;

                    playback.EventPlayed += (sender, e) =>
                    {
                        if (_outputDevice == null) return;

                        switch (e.Event)
                        {
                            case ProgramChangeEvent pc:
                                // krok34/37: Sf2-enginy (ISupportsMelodicRegister/ISupportsDrumRegister)
                                // si Program Change sledují samy a použijí ho, jen když NENÍ vynucený
                                // rejstřík (viz Sf2MidiSynth._channelProgram/GetMelodicPreset/GetDrumPreset) -
                                // tam je bezpečné posílat to vždycky, i kdyby se UniR/DrmR za běhu
                                // přepnuly zpátky na "skutečné nástroje/sadu". GS Wavetable záloha je
                                // ale jen hloupý průchod bez vlastní logiky - tam musíme Program Change
                                // sami potlačit, dokud je rejstřík vynucený. Kanál D10 (bicí) má svůj
                                // VLASTNÍ rejstřík (_drumRegister/ISupportsDrumRegister), nezávislý na
                                // _melodicRegister - viz PercussionChannel.
                                bool isDrumPc = (int)pc.Channel == PercussionChannel;
                                bool registerForced = isDrumPc ? _drumRegister != 0 : _melodicRegister != 0;
                                bool engineTracksItself = isDrumPc
                                    ? _outputDevice is ISupportsDrumRegister
                                    : _outputDevice is ISupportsMelodicRegister;
                                if (!registerForced || engineTracksItself)
                                    _outputDevice.SendEvent(e.Event);
                                break;

                            case ControlChangeEvent panCc when panCc.ControlNumber == PanControlNumber:
                                // Ignorováno - panorámu řídíme sami (ping-pong), viz výše.
                                break;

                            case ControlChangeEvent pedalCc when pedalCc.ControlNumber == 64:
                                // NOVÉ: pedál - při AJI ho emulujeme sami, jinak jde na syntezátor.
                                HandlePedal(pedalCc);
                                break;

                            case ControlChangeEvent volCc when volCc.ControlNumber == VolumeControlNumber:
                                // ZKUŠEBNĚ ignorováno (stejně jako Pan a ProgramChange
                                // výše) - dřív se tu hlasitost jednotlivé osnovy ze
                                // souboru (CC7, včetně crescend/decrescend během
                                // hraní) násobila s hlavní hlasitostí. Teď se
                                // neposílá vůbec nic, takže kanálu zůstává napořád
                                // ta pevná hodnota nastavená při startu přehrávání
                                // (100 % × hlavní hlasitost z VGA konzole, viz výše
                                // "Počáteční hlasitost..."). Zvuková dynamika
                                // jednotlivých not (velocity) tímhle není dotčená -
                                // řeší jen kanálovou/celkovou hlasitost.
                                break;

                            case SysExEvent:
                                // SysEx ze souboru (GM System On, GS Reset, XG System On...)
                                // posíláme dál beze změny - ovlivňuje zvuk (efekty, bicí),
                                // ale SOUČASNĚ vrací hlasitost (CC7 = 100) a panorámu všech
                                // kanálů na výchozí hodnoty. Bez následné obnovy by pak
                                // skladba hrála hlasitěji, než je nastaveno šipkami, a to
                                // až do prvního stisku šipky (SetVolume pošle CC7 znovu).
                                _outputDevice.SendEvent(e.Event);
                                // NOVÉ: reset ze souboru vrací i pitch bend na střed,
                                // proto zapomeneme bend ze souboru (ReassertChannelState
                                // níž pak pošle jen jemné doladění z ladění).
                                Array.Clear(_channelBendCents, 0, _channelBendCents.Length);
                                Array.Clear(_fileBendCents, 0, _fileBendCents.Length);
                                ReassertChannelState();
                                break;

                            case ControlChangeEvent resetCc when resetCc.ControlNumber == ResetAllControllersNumber:
                                // CC121 "Reset All Controllers" - stejný důvod jako u SysEx výš.
                                _outputDevice.SendEvent(resetCc);
                                // NOVÉ: CC121 vrací na střed i pitch bend (jen tohoto kanálu).
                                _channelBendCents[resetCc.Channel] = 0.0;
                                _fileBendCents[resetCc.Channel] = 0.0;
                                ReassertChannelState();
                                break;

                            case ControlChangeEvent volLsbCc when volLsbCc.ControlNumber == VolumeLsbControlNumber:
                                // CC39 = jemná část hlasitosti (LSB) kanálu - ignorujeme
                                // ze stejného důvodu jako CC7 výš.
                                break;

                            case PitchBendEvent pitchBend:
                                // Sledujeme aktuální ohyb kanálu - potřeba
                                // při NoteOn níž pro zobrazení čtvrttónových/
                                // tříčtvrttónových odchylek (viz NoteNumberToName
                                // ve FileBrowserWindow.xaml.cs). Zvukově se
                                // pořád posílá beze změny na výstupní zařízení,
                                // stejně jako dřív přes "default" větev.
                                double fileBend = (pitchBend.PitchValue - 8192) / 8192.0 * PitchBendRangeCents;

                                // NOVÉ: bend přímo ze souboru se pamatuje zvlášť (pro zobrazení
                                // not a pro návrat z AJI) - _channelBendCents je naopak to,
                                // co se právě posílá syntezátoru.
                                _fileBendCents[pitchBend.Channel] = fileBend;

                                // ZMĚNA: bend ze souboru se na syntezátor pošle SPOLU s jemným
                                // doladěním z klávesy t (SendBend je spočítá dohromady). Bicí
                                // (D10) se neladí, těm jde bend ze souboru beze změny. Při
                                // zapnutém AJI se bend ze souboru neposílá vůbec: kanály
                                // syntezátoru teď patří plánu AJI a bend by ho rozladil.
                                if (pitchBend.Channel == PercussionChannel)
                                {
                                    _outputDevice.SendEvent(pitchBend);
                                }
                                else if (!_ajiEnabled)
                                {
                                    _channelBendCents[pitchBend.Channel] = fileBend;
                                    SendBend(pitchBend.Channel);
                                }
                                break;

                            case NoteOnEvent noteOn:
                                if (noteOn.Channel == PercussionChannel && !_drumsEnabled) break;
                                if (!AjiHandleNoteOn(noteOn)) SendTunedNoteOn(noteOn);
                                // Zobrazení dostává ORIGINÁLNÍ notu ze souboru (notová
                                // osnova se tím nemění, ladění ukazuje samostatný řádek).
                                NoteOnRaised?.Invoke(noteOn.Channel, noteOn.NoteNumber, _fileBendCents[noteOn.Channel]);
                                break;

                            case NoteOffEvent noteOff:
                                if (noteOff.Channel == PercussionChannel && !_drumsEnabled) break;
                                if (!AjiHandleNoteOff(noteOff)) SendTunedNoteOff(noteOff);
                                NoteOffRaised?.Invoke(noteOff.Channel, noteOff.NoteNumber);
                                break;

                            default:
                                _outputDevice.SendEvent(e.Event);
                                break;
                        }
                    };

                    playback.Start();

                    // POZOR: podmínka "|| _isPausedByUser" je záměrná a DŮLEŽITÁ.
                    // Pause() níže volá playback.Stop() (DryWetMidi to bere jako
                    // "pozastav, ale nezahazuj pozici" - lze pak znovu Start()).
                    // Jenže playback.IsRunning je po tomhle Stop() taky false -
                    // BEZE ZKOUŠKY _isPausedByUser by tahle smyčka okamžitě
                    // skončila stejně, jako kdyby skladba doopravdy dohrála celá,
                    // a kód pod smyčkou by omylem vystřelil
                    // PlaybackFinishedNaturally (VgaEngine by pak skočila na
                    // další soubor, místo aby zůstala pozastavená na místě).
                    while (playback.IsRunning || _isPausedByUser)
                    {
                        if (token.IsCancellationRequested)
                        {
                            playback.Stop();
                            break;
                        }
                        Thread.Sleep(10);
                    }

                    // Skladba dohrála sama od sebe (nebylo to přerušené zvenčí
                    // přes Stop()) -> dáme vědět, ať VgaEngine skočí na další
                    // soubor, stejně jako to dělá u zvukových souborů.
                    if (!token.IsCancellationRequested)
                    {
                        PlaybackFinishedNaturally?.Invoke();
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // Chyba až za běhu (např. neplatná/nestandardní data
                    // uprostřed souboru) - nahlásíme a NEpokračujeme na další
                    // soubor, ať se dá problémový soubor diagnostikovat.
                    PlaybackFailed?.Invoke(ex);
                }
                finally
                {
                    _playback = null;
                }
            }, token);

            try
            {
                await _playbackTask;
            }
            catch (OperationCanceledException)
            {
                // Očekávané chování při Stop() - nejde o chybu.
            }
        }

        /// <summary>
        /// Text poslední chyby při převíjení (NENÍ fatální jako PlaybackFailed -
        /// přehrávání souboru pokračuje dál, jen konkrétní pokus o převinutí
        /// selhal). Vystaveno jako veřejná vlastnost místo jen Debug.WriteLine,
        /// protože Debug.WriteLine je v Release buildu neviditelné - a právě
        /// v Release tohle testujeme.
        /// </summary>
        public string? LastSeekDiagnostic { get; private set; }

        /// <summary>
        /// Převine přehrávání o daný počet sekund dopředu (kladné číslo) nebo
        /// dozadu (záporné). Rozehrané noty se násilím neztišují - necháme je
        /// doznít přirozeně, i za cenu drobného "mišmaše" těsně po převinutí.
        /// </summary>
        public void Seek(double deltaSeconds)
        {
            var playback = _playback;
            if (playback == null)
            {
                LastSeekDiagnostic = "Seek() zavolán, ale přehrávání zrovna neběží (_playback == null).";
                return;
            }

            try
            {
                if (deltaSeconds >= 0)
                {
                    playback.MoveForward(new MetricTimeSpan(TimeSpan.FromSeconds(deltaSeconds)));
                }
                else
                {
                    playback.MoveBack(new MetricTimeSpan(TimeSpan.FromSeconds(-deltaSeconds)));
                }

                // Po skoku v čase může Playback přehrát zpět stavové události
                // (kontrolery) z přeskočeného úseku - hlasitost a panorámu
                // proto pro jistotu nastavíme znovu.
                ReassertChannelState();

                LastSeekDiagnostic = null;
            }
            catch (Exception ex)
            {
                LastSeekDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[GmPianoMidiPlayer] Chyba při převíjení: {LastSeekDiagnostic}");
            }
        }

        /// <summary>
        /// Pozastaví přehrávání na místě, beze ztráty pozice v souboru -
        /// na rozdíl od Stop() (ten pozici zahodí a uvolní výstupní zařízení).
        /// Volá se z mezerníku ve VGA konzoli, když je aktivní MIDI režim.
        /// </summary>
        public void Pause()
        {
            _isPausedByUser = true;
            _playback?.Stop();
        }

        /// <summary>Obnoví přehrávání po Pause(), od stejného místa, kde se přerušilo.</summary>
        public void Resume()
        {
            _isPausedByUser = false;
            _playback?.Start();
        }

        /// <summary>
        /// Nastaví hlavní hlasitost (0-100), typicky navázáno na šipky
        /// nahoru/dolů ve VGA konzoli. Projeví se ihned na všech 16 kanálech
        /// a dál se násobí s případnou vlastní dynamikou (CC7) ze souboru.
        /// </summary>
        public void SetVolume(int volumePercent)
        {
            _masterVolumePercent = Math.Clamp(volumePercent, 0, 100);
            SendVolumeToAllChannels();
        }

        private void SendVolumeToAllChannels()
        {
            var device = _outputDevice;
            if (device == null) return;

            int midiVolume = ScaleToMidiVolume(127);
            for (int channel = 0; channel <= 15; channel++)
            {
                device.SendEvent(new ControlChangeEvent(
                    (SevenBitNumber)VolumeControlNumber, (SevenBitNumber)midiVolume)
                {
                    Channel = (FourBitNumber)channel
                });
            }
        }

        /// <summary>
        /// Ping-pong panoráma mezi použitými osnovami: je-li osnova jen jedna,
        /// zůstane na středu; je-li jich víc, střídají se vlevo/vpravo podle
        /// pořadí čísla kanálu.
        /// </summary>
        private void ApplyPanLayout()
        {
            var channels = _usedChannelsForPan;
            if (channels.Length == 1)
            {
                SendPan(channels[0], PanCenter);
            }
            else if (channels.Length > 1)
            {
                for (int i = 0; i < channels.Length; i++)
                {
                    SendPan(channels[i], (i % 2 == 0) ? PanLeft : PanRight);
                }
            }
        }

        /// <summary>
        /// Znovu nastaví hlasitost (hlavní hlasitost ze šipek) a panorámu všech
        /// kanálů. Volá se po všem, co je mohlo vrátit na výchozí hodnoty:
        /// SysEx reset ze souboru, CC121 a převinutí (Seek).
        /// </summary>
        private void ReassertChannelState()
        {
            SendVolumeToAllChannels();
            ApplyPanLayout();
            SendTuningBendToAllChannels();   // NOVÉ: i jemné doladění se po resetu obnoví

            // NOVÉ: panorámu kanálů právě přepsal ApplyPanLayout (a reset mohl vrátit
            // i bend), takže AJI si při další notě nastavení kanálů pošle znovu.
            Array.Fill(_poolPan, -1);
            Array.Fill(_poolBend, double.NaN);
        }

        // ------------------------------------------------------------------
        // NOVÉ: ladění - pomocné metody (viz komentář u pole _tuning výše)
        // ------------------------------------------------------------------

        /// <summary>
        /// Poskládá hodnotu pitch bendu (0-16383, střed 8192) z bendu ze
        /// souboru a jemného doladění z ladění, obojí v centech. Výsledek se
        /// ořízne do rozsahu ±200 centů (limit ±2 půltóny), takže při
        /// extrémním bendu v souboru se doladění může částečně "ztratit" -
        /// lepší než přetečení čísla.
        /// </summary>
        private static ushort ComposeBendValue(double fileBendCents, double tuningCents)
        {
            double total = fileBendCents + tuningCents;
            double value = 8192.0 + total / PitchBendRangeCents * 8192.0;
            return (ushort)Math.Clamp((int)Math.Round(value), 0, 16383);
        }

        /// <summary>Pošle kanálu pitch bend = bend ze souboru + jemné doladění. Bicí (D10) se neladí.</summary>
        private void SendBend(int channel)
        {
            var device = _outputDevice;
            if (device == null || channel == PercussionChannel) return;

            ushort value = ComposeBendValue(_channelBendCents[channel], _tuning.RemainderCents);
            device.SendEvent(new PitchBendEvent(value)
            {
                Channel = (FourBitNumber)channel
            });
        }

        private void SendTuningBendToAllChannels()
        {
            for (int channel = 0; channel <= 15; channel++)
            {
                SendBend(channel);
            }
        }

        private void SendControl(int channel, int controlNumber, int value)
        {
            _outputDevice?.SendEvent(new ControlChangeEvent((SevenBitNumber)controlNumber, (SevenBitNumber)value)
            {
                Channel = (FourBitNumber)channel
            });
        }

        /// <summary>
        /// Nastaví na všech melodických kanálech rozsah pitch bendu na ±2
        /// půltóny (RPN 0,0 = 2 půltóny + 0 centů) - na tom stojí veškerá
        /// matematika výše (PitchBendRangeCents = 200). Nakonec se vybere
        /// "nulový" RPN (127,127), aby pozdější zprávy Data Entry omylem
        /// nic nepřepsaly.
        /// </summary>
        private void SetPitchBendRangeOnAllChannels()
        {
            for (int channel = 0; channel <= 15; channel++)
            {
                if (channel == PercussionChannel) continue;

                SendControl(channel, 101, 0);     // RPN MSB
                SendControl(channel, 100, 0);     // RPN LSB  -> RPN 0,0 = Pitch Bend Sensitivity
                SendControl(channel, 6, 2);       // Data Entry MSB = 2 půltóny
                SendControl(channel, 38, 0);      // Data Entry LSB = 0 centů
                SendControl(channel, 101, 127);   // RPN null
                SendControl(channel, 100, 127);
            }
        }

        private void SendTunedNoteOn(NoteOnEvent noteOn)
        {
            var device = _outputDevice;
            if (device == null) return;

            int channel = noteOn.Channel;
            int note = noteOn.NoteNumber;

            // Bicí se neladí a nepřečíslovávají se.
            if (channel == PercussionChannel)
            {
                device.SendEvent(noteOn);
                return;
            }

            // NoteOn s nulovou velocity je podle MIDI standardu ve skutečnosti
            // NoteOff (DryWetMidi je obvykle čte rovnou jako NoteOffEvent, ale
            // pro jistotu to ošetříme i tady).
            if (noteOn.Velocity == 0)
            {
                SendTunedNoteOffCore(device, channel, note, (SevenBitNumber)0);
                return;
            }

            int sentNote = note + _tuning.WholeSemitones;

            // Mimo rozsah MIDI not (0-127) - tahle nota se při daném ladění
            // prostě nezahraje. Kdyby se stejná nota ještě držela z dřívějška,
            // zrušíme ji.
            if (sentNote < 0 || sentNote > 127)
            {
                SendTunedNoteOffCore(device, channel, note, (SevenBitNumber)0);
                return;
            }

            // Stejná nota rozeznívaná znovu, přestože předchozí pořád zní s
            // JINÝM posunem (ladění se mezitím změnilo) - tu starou nejdřív
            // vypneme, jinak by jí nikdo nikdy neposlal NoteOff.
            if (_sentNotes.TryGetValue((channel, note), out int previouslySent) && previouslySent != sentNote)
            {
                device.SendEvent(new NoteOffEvent((SevenBitNumber)previouslySent, (SevenBitNumber)0)
                {
                    Channel = noteOn.Channel
                });
            }

            _sentNotes[(channel, note)] = sentNote;

            if (sentNote == note)
            {
                device.SendEvent(noteOn);
            }
            else
            {
                // Událost ze souboru se NIKDY nemění přímo (Playback ji drží
                // a při převinutí by ji přehrál znovu už změněnou) - vyrobíme novou.
                device.SendEvent(new NoteOnEvent((SevenBitNumber)sentNote, noteOn.Velocity)
                {
                    Channel = noteOn.Channel
                });
            }
        }

        private void SendTunedNoteOff(NoteOffEvent noteOff)
        {
            var device = _outputDevice;
            if (device == null) return;

            int channel = noteOff.Channel;
            if (channel == PercussionChannel)
            {
                device.SendEvent(noteOff);
                return;
            }

            SendTunedNoteOffCore(device, channel, noteOff.NoteNumber, noteOff.Velocity);
        }

        private void SendTunedNoteOffCore(IMidiOut device, int channel, int note, SevenBitNumber velocity)
        {
            // Nota, kterou jsme nikdy nerozezněli (např. byla mimo rozsah),
            // se ani nevypíná.
            if (!_sentNotes.TryGetValue((channel, note), out int sentNote)) return;

            _sentNotes.Remove((channel, note));

            device.SendEvent(new NoteOffEvent((SevenBitNumber)sentNote, velocity)
            {
                Channel = (FourBitNumber)channel
            });
        }

        private int ScaleToMidiVolume(int originalValue0To127)
        {
            double scaled = originalValue0To127 * (_masterVolumePercent / 100.0);
            return (int)Math.Round(Math.Clamp(scaled, 0, 127));
        }

        private void SendPan(int channel, int panValue)
        {
            _outputDevice?.SendEvent(new ControlChangeEvent((SevenBitNumber)PanControlNumber, (SevenBitNumber)panValue)
            {
                Channel = (FourBitNumber)channel
            });
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();

                // KLÍČOVÉ: počkáme, až přehrávací vlákno doopravdy skončí -
                // zavolá si vlastní playback.Stop(), který ještě chce poslat
                // NoteOff na doznívající noty přes _outputDevice. Teprve POTOM
                // smíme zařízení uvolnit. Když se to dělalo v opačném pořadí
                // (uvolnit hned a nečekat), hrozila AccessViolationException,
                // protože DryWetMidi poslal NoteOff na už uvolněné nativní
                // zařízení - přesně tohle dřív padalo.
                try
                {
                    _playbackTask?.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // OperationCanceledException zabalená přes Task.Wait - očekávané.
                }

                _cts.Dispose();
                _cts = null;
            }

            _playbackTask = null;

            _outputDevice?.Dispose();
            _outputDevice = null;

            _totalTime = TimeSpan.Zero;
        }

        public void Dispose() => Stop();
    }
}
