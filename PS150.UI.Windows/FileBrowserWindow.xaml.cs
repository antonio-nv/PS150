using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PS150.Core;
using PS150.Core.Parameters;   // NOVÉ: Parameter, FieldForm
using PS150.Core.Banks;        // krok16: banky (BankTable)
using TE = PS150.Core.ToneEngine.ToneEngine;   // krok16: registr rejstříků
using PS150.Core.Input;
using PS150.Core.Network;
using PS150.Core.Tones;

namespace PS150.UI.Windows
{
    /// <summary>
    /// Náhrada za dřívější VgaEngine (textová konzole) - stejný obsah,
    /// barvy i klávesové zkratky, ale jako normální WPF okno místo
    /// Windows konzole. Důvod přepisu: konzole měla "fantomové" zalomení
    /// posledního znaku na řádku (delayed line wrap), které při pevné
    /// výšce bufferu rozjíždělo všechny absolutní souřadnice kurzoru -
    /// viz historické komentáře, které pro srovnání zůstaly u
    /// odpovídajících metod i tady. Reálné WPF okno title bar,
    /// minimalizaci, zákaz maximalizace i tažení myší dostane zadarmo od
    /// systému - nic z toho se tu ručně neřeší (na rozdíl od dřívějška).
    /// </summary>
    public partial class FileBrowserWindow : Window
    {
        // Pevný počet sloupců mřížky. Přehrávač je široký a placatý (cesta i jméno
        // souboru se mají vejít na jeden řádek bez zalomení): 121 znaků odpovídá
        // šířce mixážního pultu z návrhu. Po otočení o 90 stupňů (klávesa r) je
        // to naopak vysoký úzký pruh.
        private const int Cols = 121;

        // Záhlaví má pevných osm řádků:
        //   0 titulek okna (vlastní) + transportní tlačítka + [_] [X]
        //   1 cesta a jméno souboru (jeden řádek, bez zalomení)
        //   2 horní legenda VU metru(ů) (krok73)
        //   3 nápovědná dB stupnice VU metru(ů) (krok74, čistě dekorativní)
        //   4 VU metry L a P (krok46: teď VŽDY, i u .mid souborů - dřív se tu
        //     u .mid souborů kreslil řádek "MIDI (engine)...", VU metry se
        //     tím pádem vůbec nezobrazily, i když se peak už dávno měřil)
        //   5 dolní legenda VU metru(ů) (krok73)
        //   6 "MIDI (engine), kanálů: N" (jen u .mid souborů, jinak prázdné)
        //   7 čas, hlasitost, ladění, AJI, výstup
        // Vše další (zadávání ladění, varhanní panel, MIDI osnovy) se kreslí pod ně.
        private const int HeaderRows = 8;

        // Sloupec, kde v titulku začínají transportní tlačítka [<<] [►] [▄] [>>].
        private const int TransportCol = 20;

        private int _lastDesiredRows = -1;

        // NOVÉ: je okno aktivní? (řídí barvu titulku, viz Activated/Deactivated)
        private bool _windowActive = true;
        private DispatcherTimer _positionSaveTimer = null!;   // NOVÉ: odložené ukládání polohy okna
        // NOVÉ: ukládání polohy se zapne až po prvním vykreslení okna - ať se
        // neuloží nějaká přechodná poloha z doby, kdy se okno teprve zobrazuje.
        private bool _positionTracking = false;

        // NOVÉ: diagnostický zápis polohy okna do souboru poloha.log vedle exe
        // (pro hledání chyby; po ověření půjde smazat).
        private static void PosLog(string text)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "poloha.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {text}{Environment.NewLine}");
            }
            catch { /* log nesmí shodit program */ }
        }
        private const int StaffColumns = 4;   // NOVÉ: kanálů MIDI osnov vedle sebe v jednom řádku

        private readonly TextGridControl _grid;
        private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

        private readonly DirectoryNavigator _navigator = new();
        private readonly AudioPlayer _audioPlayer = new();
        private AppSettings _settings = AppSettings.Load();

        // Přehrávač .mid souborů na PC (natvrdo klavír přes Microsoft GS
        // Wavetable Synth) - viz GmPianoMidiPlayer.cs. Úmyslně NEpoužívá
        // PS150.Core.
        private readonly GmPianoMidiPlayer _midiPlayer = new();

        // NOVÉ: hlídá, jestli je na síti externí zvukovka (ASRock / RP5).
        // Když ano, přehrávač posílá zvuk tam; když ne (nebo zmizela),
        // hraje na zvukovku počítače. Viz OnReceiverChanged.
        private AudioReceiverWatcher? _receiverWatcher;

        private readonly object _midiNotesLock = new object();
        private readonly Dictionary<(int Channel, int Note), double> _midiActiveNotes = new();
        private int[] _midiUsedChannels = Array.Empty<int>();
        private bool _midiFinishedNaturally = false;
        private string? _midiErrorMessage = null;

        private double _leftDb = -120.0;
        private double _rightDb = -120.0;
        private int _volume = 80;
        private bool _isPaused = false;
        private bool _isMidiMode = false;

        // NOVÉ: ladění / transpozice (klávesa t nebo pole v řádku 3).
        // _tuning je neměnný objekt (viz PlayerTuning) - při změně se celý vymění.
        private PlayerTuning _tuning = PlayerTuning.Off;

        // NOVÉ: formulář s textovými poli (hlasitost, ladění, AJI...) - ovládání
        // myší (klik, kolečko), Tab, +/- a zadáním čísla. Logika je v Core
        // (PS150.Core.Parameters), tady se jen kreslí a předává vstup. INSERT se zrušil.
        private readonly FieldForm _form = new FieldForm();
        private FormField _fVolume = null!;
        private FormField _fDrums = null!;
        // krok29: pole "Lad zap/vyp" zrušeno - "vypnuto" je teď prostě T=0 a A4=440.
        private FormField _fTranspose = null!;

        // krok29: paměť pro rychlé přepnutí klávesou T (viz ToggleTuning) -
        // NEUKLÁDÁ SE do settings.json, žije jen po dobu běhu okna.
        private PlayerTuning? _tuningMemory = null;
        private FormField _fA4 = null!;
        private FormField _fAji = null!;
        private FormField _fMidiBank = null!;   // krok28: banka pro přehrávání .mid (netbicí kanály) - nezávislá na živém hraní
        private FormField _fDrumBank = null!;   // krok30: banka jen pro kanál D10 (bicí) u .mid - nezávislá na _fMidiBank i na živém hraní
        private FormField _fMelodicRegister = null!;   // krok31: univerzální tónový rejstřík (1-128 u SF2/OPL, krok44: přímo číslo rejstříku 1-999 u syntetiky), 0 = skutečné nástroje ze souboru
        private FormField _fDrumRegister = null!;   // krok37: totéž, ale bicí sada (kit) na kanálu D10, 0 = skutečná sada ze souboru

        // krok16: rejstřík = banka + číslo, stejná pole jako všechno ostatní (Tab, kolečko, +/-, číslice, Enter).
        // Pole jsou přidána AŽ ZA ostatní, aby pořadí Tab odpovídalo pořadí na obrazovce.
        // krok55: tohle byl dřív JEDEN sdílený řádek pro libovolnou banku (melodickou
        // i bicí dohromady, přepínalo se ručně v poli Banka). Teď je z něj melodický
        // řádek (Banka je omezená na nebicí banky, viz DrumBankNumber níž) a vedle
        // něj přibyl úplně stejný, ale nezávislý řádek jen pro bicí banku/rejstřík.
        // krok56: samostatné pole Zap zmizelo - zapnutí/vypnutí je teď jen "číslo +
        // Enter" (OnTypedCommit, jako dřív), a jestli je rejstřík aktivní se pozná
        // podle podbarvení v seznamu rejstříků za polem Rej (viz WrapRegisterNames).
        private FormField _fRegBank = null!;
        private FormField _fRegNumber = null!;
        private int _selBank = 1;      // vybraná MELODICKÁ banka (zatím nemusí být zapnutá)
        private int _selNumber = 1;    // vybraný rejstřík v ní

        // krok67: ladění/transpozice PRO ŽIVÉ HRANÍ - nezávislé na _fTranspose/_fA4
        // výš (to je ladění přehrávače .mid, PlayerTuning). Hodnoty samotné žijí
        // v App.OrganEngine (AudioEngine -> ToneEngine), tahle pole jsou jen UI.
        private FormField _fLiveTranspose = null!;
        private FormField _fLiveA4 = null!;

        // krok69: AJI pro ŽIVÉ hraní - viz PS150.Core.ToneEngine.LiveAjiSettings.
        // Hodnoty žijí v App.OrganEngine.LiveAji, tahle pole jsou jen UI (+ uložení
        // do settings.json, viz ApplyLiveAji*).
        private FormField _fLiveAjiMode = null!;
        private FormField _fLiveAjiTemperament = null!;
        private FormField _fLiveAjiTonic = null!;
        private FormField _fLiveAjiMinor = null!;
        private FormField _fLiveAjiWindow = null!;
        private FormField _fLiveAjiHop = null!;
        private FormField _fLiveAjiMargin = null!;
        private FormField _fLiveAjiMinHops = null!;

        // krok69: jména pro čísla výš (jen pro vykreslení, viz RenderLiveAjiRows).
        private static readonly string[] LiveAjiModeNames = { "Vyp", "Pevna", "Auto" };
        private static readonly string[] LiveAjiTemperamentNames = { "JI-5", "Stredot.", "Pythag." };
        private static readonly string[] LiveAjiTonicNames =
            { "C", "Cis", "D", "Es", "E", "F", "Fis", "G", "As", "A", "B", "H" };

        private FormField _fLiveDrmBank = null!;
        private FormField _fLiveDrmNumber = null!;
        private int _selLiveDrmBank = PS150.Core.Banks._009_SynteticBici.Number;   // vybraná BICÍ banka
        private int _selLiveDrmNumber = 1;                                        // vybraný rejstřík v ní

        // krok77/78: zkušební zadávací pole pro náhled mixpultu (viz RenderWidgetPreview) -
        // BEZ vazby na reálný zvuk, jen k vyzkoušení vzhledu a ovládání (klik/kolečko/+-/
        // zadání čísla) přímo přes pruhy metru/faderu. Hodnoty žijí jen v paměti okna,
        // nic se neukládá.
        // krok78: označení vstupu/matrixu (_demoMixerInput) NENÍ zadávací pole - jen
        // zobrazovaný text vedle skutečného metru (viz DrawMeterBar v RenderWidgetPreview),
        // proto k němu NENÍ žádný FormField.
        private double _demoMixerInput = 1.0;
        private double _demoFaderDb = -6.0;
        private readonly double[] _demoMixerField = new double[8];

        private FormField _fDemoFader = null!;
        private readonly FormField[] _fDemoMixerFields = new FormField[8];
        private static readonly string[] DemoMixerFieldNames = { "Gain", "Pan", "Gate", "Comp", "HPF", "E.Ga", "E.Fr", "E.Q" };

        // krok80: 32 pásem ekvalizéru - PŮVODNĚ (krok79) ISO třetinooktávová řada
        // (decimální "pěkná čísla" 20, 25, 31.5, 40... - průmyslový akustický standard
        // pro kalibraci reproduktorů růžovým šumem), ale ta nemá s hudební temperovanou
        // stupnicí nic společného (je založená na desítkové "preferred number" řadě,
        // poměr sousedních kmitočtů 10^(1/10) ≈ 1,259), takže nejbližší tóny vycházely
        // náhodně na D/G/B - prostý souběh dvou nesouvisejících číselných řad.
        // krok80: nahrazeno SKUTEČNOU temperovanou velkou tercií (4 půltóny, poměr
        // přesně 2^(1/3) ≈ 1,2599 - nápadně blízko tomu ISO poměru, ale tentokrát
        // doopravdy hudební), odvozenou od komorního A4 = 440Hz, přesně podle
        // uživatelovy tabulky C♯/F/A. Vychází z toho rovných 32 pásem (C♯0 = 17,3Hz
        // až F10 = 22350,6Hz), což se navíc dělí na 4 stejné skupiny po osmi
        // (8+8+8+8), ještě hezčí než předchozích 8+8+8+7.
        // krok82: kmitočty se už NEPOČÍTAJÍ tady - berou se z PS150.Core.Equalizers.
        // GraphicEqBands, což je teď JEDINÉ místo pravdy, sdílené s doopravdy
        // filtrujícím GraphicEqualizer32Band (viz PS150.Core/05_Equalizers), aby se
        // zobrazený kmitočet pásma nemohl nikdy rozejít s tím, co se skutečně filtruje.
        private static readonly double[] EqBandFreqHz = PS150.Core.Equalizers.GraphicEqBands.CenterFreqHz;
        private const int EqBandCount = PS150.Core.Equalizers.GraphicEqBands.Count;   // = 32, viz RenderEqBands

        private readonly double[] _eqBandGainDb = new double[EqBandCount];
        private readonly FormField[] _fEqBandGain = new FormField[EqBandCount];

        // krok55: jediná banka, co dnes hraje bicí - melodický řádek ji ze svého
        // výběru vylučuje, bicí řádek se naopak drží jen jí. Až vznikne druhá
        // bicí banka, přibude sem.
        private const int DrumBankNumber = PS150.Core.Banks._009_SynteticBici.Number;

        // Odložené ukládání nastavení: kolečko dělá desítky změn za sekundu,
        // settings.json se zapíše až po chvíli klidu (a při zavření okna).
        private DispatcherTimer _settingsSaveTimer = null!;

        // Nastaveno, pokud navigace (PageUp/PageDown i automatické doehrání
        // skladby) "přejede" na soubor, který vyjde jako VIDEO - okno se
        // samo zavře a MediaLauncher podle tohohle pozná, že má pokračovat
        // spuštěním MainWindow na tomhle souboru. Stejný vzor jako
        // MainWindow.HandoffFile, jen obráceně (odsud do videa).
        public string? HandoffFile { get; private set; }

        private string? _pendingFilePath;

        public FileBrowserWindow()
        {
            InitializeComponent();

            _volume = _settings.Volume;

            // NOVÉ: otočení o 90 stupňů uložené z minula (klávesa r).
            ApplyRotation(_settings.Rotated90);

            // NOVÉ: změna barvy titulku podle aktivity okna
            Activated += (s, e) => { _windowActive = true; RenderDashboard(); _grid.Redraw(); };
            Deactivated += (s, e) => { _windowActive = false; RenderDashboard(); _grid.Redraw(); };

            // NOVÉ: polohu okna (zvlášť pro vodorovný / svislý režim) obnovujeme už
            // tady, PŘED zobrazením okna - dřív se okno ukázalo na výchozím místě
            // a teprve v Loaded se přesunulo. Ukládá se průběžně (LocationChanged,
            // s odkladem), ne jen při zavření: když se otevře další soubor a
            // předchozí instance PS150 se násilně ukončí (KillPreviousInstances),
            // Window_Closing se vůbec nezavolá a poloha by se ztratila.
            WindowStartupLocation = WindowStartupLocation.Manual;
            RestoreWindowPosition();

            _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _positionSaveTimer.Tick += (s, e) =>
            {
                _positionSaveTimer.Stop();
                StoreWindowPosition();
                _settings.Save();
            };
            LocationChanged += (s, e) =>
            {
                if (!_positionTracking) return;
                _positionSaveTimer.Stop();
                _positionSaveTimer.Start();
            };
            ContentRendered += (s, e) =>
            {
                PosLog($"ContentRendered: Left={Left} Top={Top} W={ActualWidth} H={ActualHeight} rotated={_settings.Rotated90}");
                _positionTracking = true;
            };

            // NOVÉ: ladění uložené z minula (settings.json) se hned použije,
            // než se načte první soubor.
            // krok29: bez Active - uložené T/A4 se použijí přímo.
            _tuning = new PlayerTuning(
                _settings.TuningTransposeSemitones,
                _settings.TuningReferenceA4Hz);
            _midiPlayer.SetTuning(_tuning);
            _audioPlayer.SetSpeedFactor(_tuning.SpeedFactor);

            // NOVÉ: AJI uložené z minula (klávesa a).
            _midiPlayer.SetAjiEnabled(_settings.AjiEnabled);

            // krok67: ladění pro ŽIVÉ hraní uložené z minula - nezávislé na _tuning výš.
            if (App.OrganEngine != null)
            {
                App.OrganEngine.LiveTransposeSemitones = _settings.LiveTransposeSemitones;
                App.OrganEngine.LiveReferenceA4Hz = _settings.LiveReferenceA4Hz;

                // krok69: totéž pro AJI (živé hraní) - viz CreateFormFields.
                var liveAji = App.OrganEngine.LiveAji;
                liveAji.Mode = (PS150.Core.ToneEngine.LiveAjiMode)_settings.LiveAjiMode;
                liveAji.Temperament = (PS150.Core.AjiTemperament)_settings.LiveAjiTemperament;
                liveAji.FixedTonicPc = _settings.LiveAjiFixedTonicPc;
                liveAji.FixedIsMinor = _settings.LiveAjiFixedIsMinor;
                liveAji.WindowSeconds = _settings.LiveAjiWindowSeconds;
                liveAji.HopSeconds = _settings.LiveAjiHopSeconds;
                liveAji.SwitchMargin = _settings.LiveAjiSwitchMargin;
                liveAji.MinSwitchHops = _settings.LiveAjiMinSwitchHops;
                App.OrganEngine.ResetLiveAjiDetector();
            }

            // krok28: přehrávací banka pro .mid soubory uložená z minula -
            // nezávislá na bankách živého hraní. Když uložené číslo mezitím
            // přestalo být dostupné (např. jsme banku zatím nedoimplementovali),
            // tiše se vrátíme na první dostupnou, ať se nenačte ticho/záloha
            // bez vysvětlení.
            int savedPlaybackBank = GmPianoMidiPlayer.AvailablePlaybackBanks.Contains(_settings.MidiPlaybackBank)
                ? _settings.MidiPlaybackBank
                : GmPianoMidiPlayer.AvailablePlaybackBanks[0];
            _midiPlayer.SetMidiPlaybackBank(savedPlaybackBank);

            // krok30: totéž pro kanál D10 (bicí), zvlášť od banky výš.
            int savedDrumBank = GmPianoMidiPlayer.AvailableDrumBanks.Contains(_settings.MidiDrumBank)
                ? _settings.MidiDrumBank
                : GmPianoMidiPlayer.AvailableDrumBanks[0];
            _midiPlayer.SetMidiDrumBank(savedDrumBank);

            // krok31: univerzální tónový rejstřík uložený z minula (0-999,
            // viz pole UniR). Tady se netřeba hlídat proti nedostupné hodnotě
            // jako u bank výš - u SF2/OPL je celý rozsah 1-128 vždycky platný
            // (případně se sám nahradí výchozím rejstříkem, když v souboru SF2
            // zrovna není - viz Sf2MidiSynth.Create), u syntetiky se přichytí
            // na nejbližší definovaný (krok45) - a uloženou hodnotu rovnou
            // opravíme, kdyby se uložilo číslo platné jen pro jinou banku.
            _settings.MidiMelodicRegister = _midiPlayer.SetMidiMelodicRegister(_settings.MidiMelodicRegister);

            // krok37: totéž, ale pro bicí sadu (kit) na kanálu D10 - viz pole DrmR.
            _midiPlayer.SetMidiDrumRegister(_settings.MidiDrumRegister);

            // krok36: cesta k .op2/GENMIDI bance pro SoundBlaster (banku 8) je
            // teď natvrdo odvozená v GmPianoMidiPlayer (OP2\GENMIDI.op2 vedle
            // PS150.exe) - žádné nastavení přes settings.json.

            // NOVÉ: zjišťování externí zvukovky na síti (adresa je natvrdo v
            // NetworkDefaults). Chyba tady nesmí shodit start přehrávače.
            try
            {
                _receiverWatcher = new AudioReceiverWatcher(NetworkDefaults.ReceiverAddress, NetworkDefaults.Port);
                _receiverWatcher.Changed += OnReceiverChanged;
                _receiverWatcher.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileBrowserWindow] Hlídání zvukovky se nespustilo: {ex.Message}");
                _receiverWatcher = null;
            }

            _grid =new TextGridControl(Cols, 20);
            Canvas.SetLeft(_grid, 0);
            Canvas.SetTop(_grid, 0);
            RootCanvas.Children.Add(_grid);
            RootCanvas.Width = _grid.Cols * _grid.CellWidth;
            RootCanvas.Height = _grid.Rows * _grid.CellHeight;

            AddTransportButtons();
            WireMidiPlayerEvents();

            // NOVÉ: znaky zadávané do textových polí se berou z události
            // TextInput, ne z KeyDown - TextInput dostane skutečný znak podle
            // rozložení klávesnice (na české klávesnici třeba "+" nebo číslice
            // přes Shift), kdežto KeyDown zná jen fyzickou klávesu.
            TextInput += Window_TextInput;

            _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _settingsSaveTimer.Tick += (s, e) =>
            {
                _settingsSaveTimer.Stop();
                _settings.Save();
            };

            CreateFormFields();

            _tickTimer.Tick += (s, e) => Tick();
        }

        /// <summary>Obdoba MainWindow.PlayFile - volá se z MediaLauncher po Loaded, ať je okno (a tedy i mřížka) už zaručeně připravené.</summary>
        public void LoadFile(string filePath)
        {
            _pendingFilePath = filePath;
            if (IsLoaded) StartWithPendingFile();
        }

        private void StartWithPendingFile()
        {
            string filePath = _pendingFilePath ?? "";
            if (File.Exists(filePath))
            {
                _navigator.LoadDirectory(filePath);
                UpdateFileTypeState();
            }

            RenderDashboard();
            _tickTimer.Start();
        }

        private void WireMidiPlayerEvents()
        {
            _midiPlayer.NoteOnRaised += (channel, note, bendCents) =>
            {
                lock (_midiNotesLock) { _midiActiveNotes[(channel, note)] = bendCents; }
            };
            _midiPlayer.NoteOffRaised += (channel, note) =>
            {
                lock (_midiNotesLock) { _midiActiveNotes.Remove((channel, note)); }
            };
            _midiPlayer.ChannelsDetected += channels =>
            {
                lock (_midiNotesLock) { _midiUsedChannels = channels; }
            };
            _midiPlayer.PlaybackFinishedNaturally += () =>
            {
                lock (_midiNotesLock) { _midiFinishedNaturally = true; }
            };
            _midiPlayer.PlaybackFailed += ex =>
            {
                lock (_midiNotesLock) { _midiErrorMessage = ex.Message; }
            };
        }

        /// <summary>
        /// Neviditelná (průhledná) tlačítka přesně nad textem "[&lt;&lt;] [►] [▄] [&gt;&gt;]"
        /// vykresleným v mřížce - sloupcové rozsahy odpovídají 1:1 dřívějšímu
        /// IsOnTransportButton z VgaEngine. Použití reálných WPF Button
        /// prvků místo ručního přepočtu pixel->znak je jednodušší a
        /// spolehlivější (a nezávisí na tom, jestli se nakonec použil
        /// PxPlus font, nebo některý ze záložních).
        /// </summary>
        private void AddTransportButtons()
        {
            AddTransportButton(TransportCol, TransportCol + 3, (s, e) => _audioPlayer.Seek(-5.0));
            AddTransportButton(TransportCol + 5, TransportCol + 7, (s, e) => { if (_isPaused) TogglePlayPause(); RenderDashboard(); });
            AddTransportButton(TransportCol + 9, TransportCol + 11, (s, e) => { if (!_isPaused) TogglePlayPause(); RenderDashboard(); });
            AddTransportButton(TransportCol + 13, TransportCol + 16, (s, e) => _audioPlayer.Seek(5.0));
        }

        private void AddTransportButton(int colStart, int colEndInclusive, RoutedEventHandler onClick)
        {
            var button = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Width = (colEndInclusive - colStart + 1) * _grid.CellWidth,
                Height = _grid.CellHeight
            };
            button.Click += onClick;
            button.Click += (s, e) => RenderDashboard();
            Canvas.SetLeft(button, colStart * _grid.CellWidth);
            Canvas.SetTop(button, 0);   // tlačítka jsou v titulku (řádek 0)
            RootCanvas.Children.Add(button);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PosLog($"Loaded: Left={Left} Top={Top} W={ActualWidth} H={ActualHeight}");
            StartWithPendingFile();   // poloha okna je už obnovená v konstruktoru

            // OPRAVA: tady se dřív volalo KeepOnScreen(). Okno ale při startu
            // bývá chvíli vyšší (výchozí počet řádků mřížky, po načtení souboru
            // se zmenší), takže ho to posunulo nahoru od spodního okraje a
            // posunutá poloha se pak uložila. Poloha z minula se proto už
            // nijak "nesráží" - kontrolu monitorů dělá RestoreWindowPosition.
        }

        // ------------------------------------------------------------------
        // NOVÉ: vlastní titulek (řádek 0 mřížky) a otáčení o 90 stupňů
        // ------------------------------------------------------------------

        /// <summary>
        /// Klik myší: v řádku titulku [_] minimalizuje, [X] zavře, jinde se za
        /// titulek okno tahá. Souřadnice se berou přímo v prostoru mřížky
        /// (GetPosition(RootCanvas)), takže to funguje stejně i po otočení.
        /// Transportní tlačítka jsou skutečná tlačítka a událost si vezmou sama.
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(RootCanvas);
            if (p.X < 0 || p.Y < 0) return;

            int col = (int)(p.X / _grid.CellWidth);
            int row = (int)(p.Y / _grid.CellHeight);

            if (row != 0)
            {
                // NOVÉ: klik na textové pole formuláře (mimo pole se zaostření zruší)
                if (_form.Click(row, col))
                {
                    RenderDashboard();
                    e.Handled = true;
                }
                return;
            }

            if (col >= Cols - 8 && col <= Cols - 6)
            {
                WindowState = WindowState.Minimized;
                return;
            }

            if (col >= Cols - 4 && col <= Cols - 2)
            {
                Close();
                return;
            }

            try
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            }
            catch (InvalidOperationException)
            {
                // tlačítko myši mezitím povolili - tažení se nekoná
            }
        }

        /// <summary>
        /// Otočí celý obsah o 90 stupňů (o -90 stupňů, proti směru hodinových ručiček - sloupce VU metrů pak rostou zespodu nahoru). LayoutTransform
        /// otočí i rozměry, takže se okno samo přizpůsobí (SizeToContent se po
        /// přepnutí na Manual a zpět přepočítá).
        /// </summary>
        private void ApplyRotation(bool rotated)
        {
            RotationHost.LayoutTransform = rotated ? new RotateTransform(-90) : Transform.Identity;

            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.WidthAndHeight;

            // KeepOnScreen se volá jen při ručním přepnutí klávesou r (viz HandleInput)
        }

        /// <summary>Po změně tvaru okna ho posune tak, aby celé zůstalo na obrazovce.</summary>
        private void KeepOnScreen()
        {
            UpdateLayout();

            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double right = left + SystemParameters.VirtualScreenWidth;
            double bottom = top + SystemParameters.VirtualScreenHeight;

            PosLog($"KeepOnScreen: Left={Left} Top={Top} W={ActualWidth} H={ActualHeight} limit={right}x{bottom}");
            if (Left + ActualWidth > right) Left = Math.Max(left, right - ActualWidth);
            if (Top + ActualHeight > bottom) Top = Math.Max(top, bottom - ActualHeight);
        }

        /// <summary>Uloží polohu okna do položky pro aktuální (vodorovný / svislý) režim.</summary>
        private void StoreWindowPosition()
        {
            // Minimalizované okno má souřadnice kolem -32000 - takové neukládáme.
            if (WindowState != WindowState.Normal || Left < -10000 || Top < -10000) return;
            PosLog($"Store: rotated={_settings.Rotated90} Left={Left} Top={Top}");

            if (_settings.Rotated90)
            {
                _settings.RotatedWindowX = (int)Left;
                _settings.RotatedWindowY = (int)Top;
            }
            else
            {
                _settings.WindowX = (int)Left;
                _settings.WindowY = (int)Top;
            }
        }

        /// <summary>Vrací true, když se uložená poloha použila.</summary>
        private bool RestoreWindowPosition()
        {
            // NOVÉ: poloha se pamatuje zvlášť pro vodorovný a svislý režim.
            int? posX = _settings.Rotated90 ? _settings.RotatedWindowX : _settings.WindowX;
            int? posY = _settings.Rotated90 ? _settings.RotatedWindowY : _settings.WindowY;
            PosLog($"Restore: rotated={_settings.Rotated90} ulozeno={posX},{posY} (W={_settings.WindowX},{_settings.WindowY} R={_settings.RotatedWindowX},{_settings.RotatedWindowY}) obrazovka={SystemParameters.VirtualScreenLeft},{SystemParameters.VirtualScreenTop} {SystemParameters.VirtualScreenWidth}x{SystemParameters.VirtualScreenHeight}");
            if (!posX.HasValue || !posY.HasValue) return false;

            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = SystemParameters.VirtualScreenWidth;
            double vHeight = SystemParameters.VirtualScreenHeight;

            // Necháváme aspoň kousek okna viditelný, ať je za co ho příště
            // chytit - stejná kontrola jako dřív ve VgaEngine.Run().
            const int minVisible = 50;
            bool withinBounds =
                posX.Value >= vLeft - minVisible &&
                posX.Value <= vLeft + vWidth - minVisible &&
                posY.Value >= vTop &&
                posY.Value <= vTop + vHeight - minVisible;

            PosLog($"Restore: withinBounds={withinBounds}");
            if (withinBounds)
            {
                Left = posX.Value;
                Top = posY.Value;
            }

            return withinBounds;
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _tickTimer.Stop();
            _positionSaveTimer.Stop();
            _settingsSaveTimer.Stop();   // nastavení se uloží níž tak jako tak

            StoreWindowPosition();

            string? lastFile = HandoffFile ?? _navigator.CurrentFile;
            if (!string.IsNullOrEmpty(lastFile))
            {
                _settings.LastFilePath = lastFile;
                _settings.LastFolderPath = Path.GetDirectoryName(lastFile);
            }
            _settings.Save();

            // NOVÉ: nejdřív zastavit hlídání sítě, ať už nic nesahá na přehrávač.
            _receiverWatcher?.Dispose();
            _receiverWatcher = null;

            _audioPlayer.Stop();
            _audioPlayer.Dispose();
            _midiPlayer.Stop();
        }

        /// <summary>
        /// NOVÉ: externí zvukovka se objevila (info) nebo zmizela (null).
        /// Přichází z vlákna hlídače, proto se přepíná přes Dispatcher.
        /// </summary>
        private void OnReceiverChanged(ReceiverInfo? info)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                IPEndPoint? endpoint = info != null ? _receiverWatcher?.Endpoint : null;
                _audioPlayer.SetNetworkTarget(endpoint, endpoint != null ? info : null);
                _grid.Clear();
            }));
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // NOVÉ: kolečko nad textovým polem mění jeho hodnotu, jinde hlasitost jako dřív.
            Point p = e.GetPosition(RootCanvas);
            if (p.X >= 0 && p.Y >= 0)
            {
                int col = (int)(p.X / _grid.CellWidth);
                int row = (int)(p.Y / _grid.CellHeight);

                int notches = e.Delta / 120;
                if (notches == 0) notches = Math.Sign(e.Delta);

                if (_form.Wheel(row, col, notches, CurrentStepSize()))
                {
                    RenderDashboard();
                    e.Handled = true;
                    return;
                }
            }

            ChangeVolume(e.Delta > 0 ? 5 : -5);
            RenderDashboard();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Alt+F4 - stejná hláška jako v MainWindow.xaml.cs. Bez týhle
            // výjimky by ho spolykalo e.Handled = true níž (nastavuje se
            // po každé klávese, ať HandleInput nemusí řešit e.Handled samo) -
            // tím by se systémový příkaz na zavření okna nikdy nedostal dál.
            if (e.Key == Key.System && e.SystemKey == Key.F4)
            {
                Close();
                return;
            }

            // NOVÉ: textová pole formuláře. Řídicí klávesy (Tab, Enter, Esc, Backspace,
            // Ctrl+PgUp/PgDn) vyřídí formulář. ZNAKOVÉ klávesy se u zaostřeného pole
            // záměrně NEoznačí jako vyřízené (e.Handled zůstane false) - jedině tak
            // WPF vyvolá událost TextInput, která znak doručí do Window_TextInput níž.
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // ZMĚNA: velikost kroku podle Shift (hrubý) / Ctrl (jemný). Ctrl + numerické +/-
            // nebo Ctrl + minus = jemný krok (znak z nich TextInput nedoručí, proto tady).
            bool fineKey = ctrl && _form.HasFocus && !_form.Editing
                && (e.Key == Key.Add || e.Key == Key.Subtract || e.Key == Key.OemPlus || e.Key == Key.OemMinus);
            if (fineKey)
            {
                bool up = e.Key == Key.Add || e.Key == Key.OemPlus;
                _form.HandleKey(up ? FormKey.NudgeUp : FormKey.NudgeDown, StepSize.Fine);
                e.Handled = true;
                RenderDashboard();
                return;
            }

            bool handledByForm = e.Key switch
            {
                Key.Tab => _form.HandleKey(shift ? FormKey.ShiftTab : FormKey.Tab),
                Key.Enter => _form.HandleKey(FormKey.Enter),
                Key.Escape => _form.HandleKey(FormKey.Escape),
                Key.Back => _form.HandleKey(FormKey.Backspace),
                Key.PageUp when ctrl => _form.HandleKey(FormKey.NudgeUp, shift ? StepSize.Coarse : StepSize.Normal),
                Key.PageDown when ctrl => _form.HandleKey(FormKey.NudgeDown, shift ? StepSize.Coarse : StepSize.Normal),
                Key.Delete => _form.HandleKey(FormKey.Reset),
                _ => false
            };

            if (handledByForm)
            {
                e.Handled = true;
                RenderDashboard();
                return;
            }

            if (_form.HasFocus)
            {
                if (IsFieldCharKey(e.Key)) return;   // znak doručí TextInput

                if (_form.Editing)
                {
                    e.Handled = true;                // při zadávání čísla nic jiného nefunguje
                    return;
                }
            }

            bool keepRunning = HandleInput(e.Key);
            RenderDashboard();
            e.Handled = true;

            if (!keepRunning) Close();
        }

        /// <summary>Obdoba hlavní smyčky v dřívějším VgaEngine.Run() - jen bez čtení vstupu (to teď řeší Window_KeyDown/MouseWheel/tlačítka výše).</summary>
        private void Tick()
        {
            if (!_isMidiMode && !_isPaused && _audioPlayer.TotalTime > TimeSpan.Zero)
            {
                if (_audioPlayer.CurrentTime >= _audioPlayer.TotalTime - TimeSpan.FromMilliseconds(300))
                {
                    AdvanceToNextFile();
                    if (HandoffFile != null) { Close(); return; }
                }
            }
            else if (_isMidiMode)
            {
                bool finishedNaturally;
                lock (_midiNotesLock)
                {
                    finishedNaturally = _midiFinishedNaturally;
                    _midiFinishedNaturally = false;
                }

                if (finishedNaturally)
                {
                    AdvanceToNextFile();
                    if (HandoffFile != null) { Close(); return; }
                }
            }

            // krok46: L/R VU metr - u normálních souborů z AudioPlayer, u .mid
            // souborů teď z _midiPlayer (Sf2MidiSynth/OplMidiSynth/
            // SyntheticMidiSynth přes DualEngineMidiOut - viz
            // IProvidesAudioMeter). Dřív se u .mid souborů měření přeskakovalo
            // úplně - to byl ještě komentář z dob, kdy .mid hrálo čistě přes
            // Windows GS Wavetable Synth (externí, nepřístupné vzorky) a
            // doopravdy nebylo co měřit. Teď vlastní vzorky máme, takže se
            // měří pořád, jen ze dvou různých zdrojů podle režimu.
            var (peakL, peakR) = _isMidiMode ? _midiPlayer.ReadPeakLevels() : _audioPlayer.ReadPeakLevels();
            float organPeakForLR = App.OrganEngine?.ReadPeak() ?? 0f;
            _leftDb = AudioMeter.LinearToDecibels(Math.Max(peakL, organPeakForLR));
            _rightDb = AudioMeter.LinearToDecibels(Math.Max(peakR, organPeakForLR));

            // Záhlaví (včetně VU metrů) se kreslí až po změření úrovní výše.
            RenderDashboard();
            RenderStatusArea();

            _grid.Redraw();
        }

        /// <summary>Nastaví HandoffFile a přepne/aktualizuje aktuální soubor - použito jak z Tick() (doehrání), tak z HandleInput (PageUp/PageDown).</summary>
        private void AdvanceToNextFile(bool forward = true)
        {
            string? nextFile = forward ? _navigator.GetNextFile() : _navigator.GetPreviousFile();
            if (nextFile == null) return;

            if (MediaKind.IsVideo(nextFile))
            {
                HandoffFile = nextFile;
                return;
            }

            UpdateFileTypeState();
            _grid.Clear();
        }

        private void TogglePlayPause()
        {
            _isPaused = !_isPaused;

            if (_isMidiMode)
            {
                if (_isPaused) _midiPlayer.Pause();
                else _midiPlayer.Resume();
            }
            else
            {
                if (_isPaused) _audioPlayer.Pause();
                else _audioPlayer.Play();
            }
        }

        private void ChangeVolume(int delta)
        {
            SetVolume(_volume + delta);
        }

        private void SetVolume(int volume)
        {
            _volume = Math.Clamp(volume, 0, 100);
            _audioPlayer.Volume = _volume / 100.0f;
            _midiPlayer.SetVolume(_volume);

            _settings.Volume = _volume;
            ScheduleSettingsSave();
        }

        private void ScheduleSettingsSave()
        {
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        private void UpdateFileTypeState()
        {
            string currentFile = _navigator.CurrentFile ?? "";
            string ext = Path.GetExtension(currentFile).ToLowerInvariant();

            _isMidiMode = (ext == ".mid" || ext == ".midi" || ext == ".kar");

            if (_isMidiMode)
            {
                _audioPlayer.Stop();
                _isPaused = false;

                lock (_midiNotesLock)
                {
                    _midiActiveNotes.Clear();
                    _midiUsedChannels = Array.Empty<int>();
                    _midiFinishedNaturally = false;
                    _midiErrorMessage = null;
                }

                if (File.Exists(currentFile))
                {
                    // Hlasitost se musí nastavit i PŘED PlayAsync: nový přehrávač
                    // má výchozích 100 % a PlayAsync z něj při startu rozešle
                    // hlasitost všem kanálům - první noty by tak mohly zaznít
                    // naplno, než dojde na SetVolume níž.
                    _midiPlayer.SetVolume(_volume);
                    _ = _midiPlayer.PlayAsync(currentFile);
                    _midiPlayer.SetVolume(_volume);
                    _midiPlayer.SetDrumsEnabled(_settings.DrumsEnabled);
                }
            }
            else
            {
                _midiPlayer.Stop();

                if (File.Exists(currentFile))
                {
                    _audioPlayer.Load(currentFile);
                    _audioPlayer.Volume = _volume / 100.0f;
                    _audioPlayer.Play();
                    _isPaused = false;
                }
            }
        }

        /// <returns>false = má se okno zavřít (stejná konvence jako dřívější HandleInput(ConsoleKey) : bool v VgaEngine).</returns>
        private bool HandleInput(Key key)
        {
            // krok16: číslice bez zaostřeného pole jdou rovnou do pole Rejstřík ("85 Enter" funguje jako dřív)
            if ((key >= Key.D0 && key <= Key.D9) || (key >= Key.NumPad0 && key <= Key.NumPad9))
            {
                char digit = (char)('0' + (key >= Key.NumPad0 ? key - Key.NumPad0 : key - Key.D0));
                _form.FocusAndType(_fRegNumber, digit);
                return true;
            }

            switch (key)
            {
                case Key.Escape:
                    return false;

                case Key.Space:
                case Key.X:
                case Key.C:
                case Key.V:
                    TogglePlayPause();
                    break;

                case Key.PageDown:
                case Key.B:
                    AdvanceToNextFile(forward: true);
                    if (HandoffFile != null) return false;
                    break;

                case Key.PageUp:
                case Key.Z:
                    AdvanceToNextFile(forward: false);
                    if (HandoffFile != null) return false;
                    break;

                case Key.Right:
                    if (_isMidiMode) _midiPlayer.Seek(5.0);
                    else _audioPlayer.Seek(5.0);
                    break;

                case Key.Left:
                    if (_isMidiMode) _midiPlayer.Seek(-5.0);
                    else _audioPlayer.Seek(-5.0);
                    break;

                case Key.D:
                    if (_isMidiMode)
                    {
                        bool newState = !_midiPlayer.DrumsEnabled;
                        _midiPlayer.SetDrumsEnabled(newState);
                        _settings.DrumsEnabled = newState;
                        _settings.Save();
                    }
                    break;

                case Key.Up:
                    ChangeVolume(5);
                    break;

                case Key.Down:
                    ChangeVolume(-5);
                    break;

                case Key.T:
                    // krok29: ladění zapnout/vypnout beze ztráty hodnot - viz ToggleTuning.
                    ToggleTuning();
                    break;

                case Key.A:
                    // NOVÉ: AJI (adaptivní čisté ladění) zapnout/vypnout za chodu.
                    // Týká se jen .mid souborů; nastavení se uloží.
                    _midiPlayer.SetAjiEnabled(!_midiPlayer.AjiEnabled);
                    _settings.AjiEnabled = _midiPlayer.AjiEnabled;
                    _settings.Save();
                    _grid.Clear();
                    break;

                case Key.R:
                    // NOVÉ: otočit okno o 90 stupňů / zpět (nastavení se pamatuje)
                    StoreWindowPosition();                 // poloha starého režimu
                    _settings.Rotated90 = !_settings.Rotated90;
                    ApplyRotation(_settings.Rotated90);
                    if (!RestoreWindowPosition())          // poloha nového režimu (je-li uložená)
                    {
                        KeepOnScreen();                    // jinak aspoň nevyčnívat z obrazovky
                    }
                    _settings.Save();
                    break;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // NOVÉ: ladění / transpozice
        // ------------------------------------------------------------------

        /// <summary>
        /// Použije nové ladění všude, kde se projeví: u MIDI přes pitch bend,
        /// u zvukových souborů změnou rychlosti. Uloží ho do settings.json
        /// a vymaže mřížku, protože se tím mění počet řádků na obrazovce.
        /// </summary>
        private void ApplyTuning(PlayerTuning tuning)
        {
            _tuning = tuning;

            _midiPlayer.SetTuning(tuning);
            _audioPlayer.SetSpeedFactor(tuning.SpeedFactor);

            _settings.TuningTransposeSemitones = tuning.TransposeSemitones;
            _settings.TuningReferenceA4Hz = tuning.ReferenceA4Hz;
            ScheduleSettingsSave();
        }

        /// <summary>
        /// krok67: totéž jako ApplyTuning výš, ale pro ŽIVÉ hraní (App.OrganEngine) -
        /// úmyslně nezávislé na _tuning/ApplyTuning (přehrávač .mid). Hodnoty žijí
        /// přímo v AudioEngine/ToneEngine, tady se jen zapíšou + uloží do settings.json.
        /// </summary>
        private void ApplyLiveTuning(double transposeSemitones, double referenceA4Hz)
        {
            if (App.OrganEngine == null) return;

            App.OrganEngine.LiveTransposeSemitones = transposeSemitones;
            App.OrganEngine.LiveReferenceA4Hz = referenceA4Hz;

            _settings.LiveTransposeSemitones = transposeSemitones;
            _settings.LiveReferenceA4Hz = referenceA4Hz;
            ScheduleSettingsSave();
        }

        /// <summary>
        /// krok69: po jakékoliv změně AJI nastavení pro ŽIVÉ hraní (App.OrganEngine.
        /// LiveAji.* se upraví přímo v setteru pole - viz CreateFormFields) tohle
        /// uloží settings.json a hlavně přepočítá právě podržené tóny (ApplyLiveAji),
        /// ať se to projeví okamžitě - stejný "glissando" princip jako krok68.
        /// resetDetector: true u všeho, co mění, JAK se AutoDetect dívá na minulost
        /// (mód, okno, krok, práh, minKroků) - ať nezůstane napůl rozkoukaný podle
        /// starých pravidel.
        /// </summary>
        private void ApplyLiveAjiAndSave(bool resetDetector)
        {
            if (resetDetector) App.OrganEngine?.ResetLiveAjiDetector();
            App.OrganEngine?.ApplyLiveAji();
            ScheduleSettingsSave();
        }

        /// <summary>
        /// krok29: náhrada za starý samostatný přepínač "Lad zap/vyp".
        /// Když je ladění zrovna nastavené na něco (T!=0 nebo A4!=440),
        /// zapamatuje si to a přepne na neutrál (0 st / 440 Hz).
        /// Když je zrovna neutrál a něco je zapamatované z dřívějška v tomhle
        /// běhu okna, vrátí to zpátky. Jinak (neutrál a nic v paměti) nedělá nic.
        /// </summary>
        private void ToggleTuning()
        {
            if (!_tuning.IsNeutral)
            {
                _tuningMemory = _tuning;
                ApplyTuning(PlayerTuning.Off);
            }
            else if (_tuningMemory != null)
            {
                ApplyTuning(_tuningMemory);
            }
        }

        // ------------------------------------------------------------------
        // NOVÉ: textová pole formuláře (hlasitost, bicí, ladění, AJI)
        // ------------------------------------------------------------------

        private void CreateFormFields()
        {
            _fVolume = _form.Add(Parameter.Number(
                "Vol", "%", 0, 100, 0, new[] { 1.0, 5.0, 10.0},
                () => _volume,
                v => SetVolume((int)Math.Round(v))));

            _fDrums = _form.Add(Parameter.Toggle(
                "Bicí",
                () => _midiPlayer.DrumsEnabled,
                on =>
                {
                    _midiPlayer.SetDrumsEnabled(on);
                    _settings.DrumsEnabled = on;
                    ScheduleSettingsSave();
                }));

            // krok29: samostatné pole "Lad zap/vyp" zrušeno - stačí dát T na 0
            // (a A4 na 440), viz PlayerTuning.IsNeutral. Klávesa T dál dělá
            // rychlé přepnutí tam a zpátky, viz ToggleTuning().
            _fTranspose = _form.Add(Parameter.Number(
                "T", "st",
                -PlayerTuning.MaxTransposeSemitones, PlayerTuning.MaxTransposeSemitones, 2,
                new[] { 0.01, 0.5, 1.0 },
                () => _tuning.TransposeSemitones,
                v => ApplyTuning(new PlayerTuning(v, _tuning.ReferenceA4Hz)),
                showSign: true,
                defaultValue: 0.0));   // '*' vrátí transpozici na nulu

            _fA4 = _form.Add(Parameter.Number(
                "A4", "Hz",
                PlayerTuning.MinReferenceA4Hz, PlayerTuning.MaxReferenceA4Hz, 1,
                new[] { 0.1, 1.0, 5.0 },
                () => _tuning.ReferenceA4Hz,
                v => ApplyTuning(new PlayerTuning(_tuning.TransposeSemitones, v)),
                defaultValue: 440.0));   // '*' vrátí A4 na 440 Hz

            _fAji = _form.Add(Parameter.Toggle(
                "AJI",
                () => _midiPlayer.AjiEnabled,
                on =>
                {
                    _midiPlayer.SetAjiEnabled(on);
                    _settings.AjiEnabled = on;
                    ScheduleSettingsSave();
                }));

            // krok28: banka (engine) pro přehrávání NETBICÍCH kanálů .mid souborů -
            // nezávislá na bankách živého hraní (pole "Banka"/"Rej" výš jsou pro
            // App.OrganEngine). Projeví se od příštího spuštění souboru, ne za běhu.
            var pMidiBank = Parameter.Number(
                "MidB", "", 0, 999, 0, new[] { 1.0, 1.0, 1.0 },
                () => _settings.MidiPlaybackBank,
                v =>
                {
                    int bank = (int)Math.Round(v);
                    _midiPlayer.SetMidiPlaybackBank(bank);
                    _settings.MidiPlaybackBank = bank;
                    ScheduleSettingsSave();
                },
                zeroPad: 3);
            pMidiBank.Stepper = (cur, dir) => StepInList(GmPianoMidiPlayer.AvailablePlaybackBanks, cur, dir);
            _fMidiBank = _form.Add(pMidiBank);

            // krok31: univerzální tónový rejstřík pro netbicí kanály .mid souboru -
            // u SF2/OPL bank "lidské" GM číslování 1-128 (1 = Acoustic Grand
            // Piano, staré natvrdo zapsané chování). 0 = žádný univerzální
            // rejstřík - hrají skutečné nástroje podle Program Change v souboru
            // (viz Sf2MidiSynth).
            // krok37: pole UniR teď navazuje hned na MidB (banka + její rejstřík
            // pohromadě), DrmB/DrmR mají stejnou dvojici hned za sebou níž.
            // krok44: rozsah rozšířen na 0-999 - u syntetické banky (MidB=001)
            // totiž tohle pole neznamená GM program, ale PŘÍMO číslo rejstříku
            // z ToneEngine (200 = Piano Petrof, 300 = Cembalo, 600 = Strings...
            // viz ISupportsSyntheticRegister/SyntheticMidiSynth) - 0-128 by na
            // taková čísla nestačilo a zadání by skončilo červeně jako chyba.
            var pMelodicRegister = Parameter.Number(
                "UniR", "", 0, 999, 0, new[] { 1.0, 1.0, 10.0 },
                () => _settings.MidiMelodicRegister,
                v =>
                {
                    int program = (int)Math.Round(v);
                    // krok45: SetMidiMelodicRegister vrací efektivně použité číslo -
                    // u syntetiky se zadání může "přichytit" na nejbližší skutečně
                    // definovaný rejstřík (viz komentář u metody), takže se do pole/
                    // settings uloží TOHLE, ne to, co se doslova naťukalo.
                    int effective = _midiPlayer.SetMidiMelodicRegister(program);
                    _settings.MidiMelodicRegister = effective;
                    ScheduleSettingsSave();
                },
                zeroPad: 3,
                defaultValue: 1.0);   // '*' vrátí na výchozí rejstřík č. 1 (Acoustic Grand Piano u SF2/OPL, Bombard16 u syntetiky)
            _fMelodicRegister = _form.Add(pMelodicRegister);

            // krok30: totéž, ale jen pro kanál D10 (bicí) - viz DualEngineMidiOut.
            // Stejný princip jako MidB, úplně nezávislé pole/nastavení.
            var pDrumBank = Parameter.Number(
                "DrmB", "", 0, 999, 0, new[] { 1.0, 1.0, 1.0 },
                () => _settings.MidiDrumBank,
                v =>
                {
                    int bank = (int)Math.Round(v);
                    _midiPlayer.SetMidiDrumBank(bank);
                    _settings.MidiDrumBank = bank;
                    ScheduleSettingsSave();
                },
                zeroPad: 3);
            pDrumBank.Stepper = (cur, dir) => StepInList(GmPianoMidiPlayer.AvailableDrumBanks, cur, dir);
            _fDrumBank = _form.Add(pDrumBank);

            // krok37: bicí rejstřík (kit) pro kanál D10 u .mid souboru - stejná
            // logika jako UniR výš, jen pro bicí sadu (viz Sf2MidiSynth.
            // SetDrumProgram/ISupportsDrumRegister). "Lidské" GM/GS číslování
            // 1-128 (1 = Standard kit, staré natvrdo zapsané chování P000). 0 =
            // žádný vynucený rejstřík - hraje skutečná sada podle Program
            // Change na kanálu D10 v souboru. U OPL (SoundBlaster) banky se
            // tohle pole neprojeví - formát .op2 má jen jednu univerzální bicí
            // mapu, žádné přepínání sad v něm není (viz OplMidiSynth).
            var pDrumRegister = Parameter.Number(
                "DrmR", "", 0, 128, 0, new[] { 1.0, 1.0, 10.0 },
                () => _settings.MidiDrumRegister,
                v =>
                {
                    int program = (int)Math.Round(v);
                    _midiPlayer.SetMidiDrumRegister(program);
                    _settings.MidiDrumRegister = program;
                    ScheduleSettingsSave();
                },
                zeroPad: 3,
                defaultValue: 1.0);   // '*' vrátí na výchozí Standard kit
            _fDrumRegister = _form.Add(pDrumRegister);

            // ---- krok67: ladění/transpozice pro ŽIVÉ hraní - vlastní řádek, nezávislý
            // na ladění přehrávače .mid (_fTranspose/_fA4 výš). Hodnoty žijí přímo
            // v App.OrganEngine, pole je jen čtou/zapisují (viz ApplyLiveTuning). ----
            var pLiveTranspose = Parameter.Number(
                "T", "st",
                -PlayerTuning.MaxTransposeSemitones, PlayerTuning.MaxTransposeSemitones, 2,
                new[] { 0.01, 0.5, 1.0 },
                () => App.OrganEngine?.LiveTransposeSemitones ?? 0.0,
                v => ApplyLiveTuning(v, App.OrganEngine?.LiveReferenceA4Hz ?? 440.0),
                showSign: true,
                defaultValue: 0.0);   // '*' vrátí transpozici na nulu
            _fLiveTranspose = _form.Add(pLiveTranspose);

            var pLiveA4 = Parameter.Number(
                "A4", "Hz",
                PlayerTuning.MinReferenceA4Hz, PlayerTuning.MaxReferenceA4Hz, 1,
                new[] { 0.1, 1.0, 5.0 },
                () => App.OrganEngine?.LiveReferenceA4Hz ?? 440.0,
                v => ApplyLiveTuning(App.OrganEngine?.LiveTransposeSemitones ?? 0.0, v),
                defaultValue: 440.0);   // '*' vrátí A4 na 440 Hz
            _fLiveA4 = _form.Add(pLiveA4);

            // ---- krok69: AJI pro ŽIVÉ hraní - "čím víc nastavování, tím líp" ----
            // krok69: NEukládat App.OrganEngine?.LiveAji do proměnné tady nahoře -
            // volá se to znovu při KAŽDÉM čtení/zápisu (stejně jako u ostatních polí
            // výš, např. LiveTransposeSemitones), aby se nezatuhlo na null, kdyby
            // tahle metoda náhodou proběhla dřív, než se App.OrganEngine vůbec stihl
            // nastavit.
            PS150.Core.ToneEngine.LiveAjiSettings? Aji() => App.OrganEngine?.LiveAji;

            var pLiveAjiMode = Parameter.Number(
                "Mod", "", 0, 2, 0, new[] { 1.0, 1.0, 1.0 },
                () => (double)(Aji()?.Mode ?? PS150.Core.ToneEngine.LiveAjiMode.Off),
                v =>
                {
                    if (Aji() == null) return;
                    Aji().Mode = (PS150.Core.ToneEngine.LiveAjiMode)(int)Math.Round(v);
                    _settings.LiveAjiMode = (int)Aji().Mode;
                    ApplyLiveAjiAndSave(resetDetector: true);
                },
                defaultValue: 0.0);
            _fLiveAjiMode = _form.Add(pLiveAjiMode);

            var pLiveAjiTemperament = Parameter.Number(
                "Lad", "", 0, 2, 0, new[] { 1.0, 1.0, 1.0 },
                () => (double)(Aji()?.Temperament ?? PS150.Core.AjiTemperament.JustIntonation5Limit),
                v =>
                {
                    if (Aji() == null) return;
                    Aji().Temperament = (PS150.Core.AjiTemperament)(int)Math.Round(v);
                    _settings.LiveAjiTemperament = (int)Aji().Temperament;
                    ApplyLiveAjiAndSave(resetDetector: false);
                },
                defaultValue: 0.0);
            _fLiveAjiTemperament = _form.Add(pLiveAjiTemperament);

            var pLiveAjiTonic = Parameter.Number(
                "Ton", "", 0, 11, 0, new[] { 1.0, 1.0, 1.0 },
                () => Aji()?.FixedTonicPc ?? 0,
                v =>
                {
                    if (Aji() == null) return;
                    Aji().FixedTonicPc = (int)Math.Round(v);
                    _settings.LiveAjiFixedTonicPc = Aji().FixedTonicPc;
                    ApplyLiveAjiAndSave(resetDetector: false);
                },
                defaultValue: 0.0);
            _fLiveAjiTonic = _form.Add(pLiveAjiTonic);

            var pLiveAjiMinor = Parameter.Toggle(
                "Moll",
                () => Aji()?.FixedIsMinor ?? false,
                on =>
                {
                    if (Aji() == null) return;
                    Aji().FixedIsMinor = on;
                    _settings.LiveAjiFixedIsMinor = on;
                    ApplyLiveAjiAndSave(resetDetector: false);
                });
            _fLiveAjiMinor = _form.Add(pLiveAjiMinor);

            // ---- krok69: parametry AutoDetect - "ať se v tom teoretici můžou šťourat" ----
            var pLiveAjiWindow = Parameter.Number(
                "Okno", "s", 1.0, 60.0, 1, new[] { 0.5, 1.0, 5.0 },
                () => Aji()?.WindowSeconds ?? PS150.Core.AjiPlanner.WindowSeconds,
                v =>
                {
                    if (Aji() == null) return;
                    Aji().WindowSeconds = v;
                    _settings.LiveAjiWindowSeconds = v;
                    ApplyLiveAjiAndSave(resetDetector: true);
                },
                defaultValue: PS150.Core.AjiPlanner.WindowSeconds);
            _fLiveAjiWindow = _form.Add(pLiveAjiWindow);

            var pLiveAjiHop = Parameter.Number(
                "Krok", "s", 0.2, 10.0, 1, new[] { 0.1, 0.5, 2.0 },
                () => Aji()?.HopSeconds ?? PS150.Core.AjiPlanner.HopSeconds,
                v =>
                {
                    if (Aji() == null) return;
                    Aji().HopSeconds = v;
                    _settings.LiveAjiHopSeconds = v;
                    ApplyLiveAjiAndSave(resetDetector: true);
                },
                defaultValue: PS150.Core.AjiPlanner.HopSeconds);
            _fLiveAjiHop = _form.Add(pLiveAjiHop);

            var pLiveAjiMargin = Parameter.Number(
                "Prah", "", 0.0, 1.0, 2, new[] { 0.01, 0.05, 0.1 },
                () => Aji()?.SwitchMargin ?? PS150.Core.AjiPlanner.SwitchMargin,
                v =>
                {
                    if (Aji() == null) return;
                    Aji().SwitchMargin = v;
                    _settings.LiveAjiSwitchMargin = v;
                    ApplyLiveAjiAndSave(resetDetector: true);
                },
                defaultValue: PS150.Core.AjiPlanner.SwitchMargin);
            _fLiveAjiMargin = _form.Add(pLiveAjiMargin);

            var pLiveAjiMinHops = Parameter.Number(
                "MinKr", "", 1, 20, 0, new[] { 1.0, 1.0, 5.0 },
                () => Aji()?.MinSwitchHops ?? PS150.Core.AjiPlanner.MinSwitchHops,
                v =>
                {
                    if (Aji() == null) return;
                    Aji().MinSwitchHops = (int)Math.Round(v);
                    _settings.LiveAjiMinSwitchHops = Aji().MinSwitchHops;
                    ApplyLiveAjiAndSave(resetDetector: true);
                },
                defaultValue: PS150.Core.AjiPlanner.MinSwitchHops);
            _fLiveAjiMinHops = _form.Add(pLiveAjiMinHops);

            // ---- krok16/krok55: banka, rejstřík, zap/vyp - melodický řádek ----
            // Banka smí být jen NEBICÍ (DrumBankNumber vyřazen) - bicí má svůj vlastní
            // řádek kus níž, ať se živé hraní neplete melodii s bicí soupravou.
            var melBanks = TE.Banks.Where(b => b != DrumBankNumber).ToList();
            _selBank = melBanks.Count > 0 ? melBanks[0] : 1;
            var nums = TE.NumbersInBank(_selBank);
            _selNumber = nums.Count > 0 ? nums[0] : 0;

            var pBank = Parameter.Number(
                "Banka", "", 0, 999, 0, new[] { 1.0, 1.0, 10.0 },
                () => _selBank,
                v => SelectBank((int)Math.Round(v)),
                zeroPad: 3);
            pBank.Stepper = (cur, dir) => StepInList(melBanks, cur, dir);   // +/- procházejí jen melodické banky
            _fRegBank = _form.Add(pBank);

            var pNum = Parameter.Number(
                "Rej", "", 0, 99999, 0, new[] { 1.0, 1.0, 10.0 },   // krok17: až 99999 (čísla z SF2 bank, např. 8040)
                () => _selNumber,
                v => _selNumber = (int)Math.Round(v),
                zeroPad: 3);
            pNum.Stepper = (cur, dir) => StepInList(TE.NumbersInBank(_selBank), cur, dir);   // +/- další existující rejstřík
            pNum.OnTypedCommit = v => ToggleTypedRegister((int)Math.Round(v));               // "85 Enter" přepne rejstřík
            pNum.OnStepCommit = v => StepRegister(_selBank, (int)Math.Round(v));             // krok70: +/- přepne rovnou, bez Enter
            _fRegNumber = _form.Add(pNum);

            // ---- krok55: úplně stejná dvojice polí, ale nezávislá a jen pro bicí banku ----
            var drmBanks = TE.Banks.Where(b => b == DrumBankNumber).ToList();
            _selLiveDrmBank = drmBanks.Count > 0 ? drmBanks[0] : DrumBankNumber;
            var drmNums = TE.NumbersInBank(_selLiveDrmBank);
            _selLiveDrmNumber = drmNums.Count > 0 ? drmNums[0] : 0;

            var pDrmBank = Parameter.Number(
                "Banka", "", 0, 999, 0, new[] { 1.0, 1.0, 10.0 },
                () => _selLiveDrmBank,
                v => SelectDrmBank((int)Math.Round(v)),
                zeroPad: 3);
            pDrmBank.Stepper = (cur, dir) => StepInList(drmBanks, cur, dir);
            _fLiveDrmBank = _form.Add(pDrmBank);

            var pDrmNum = Parameter.Number(
                "Rej", "", 0, 99999, 0, new[] { 1.0, 1.0, 10.0 },
                () => _selLiveDrmNumber,
                v => _selLiveDrmNumber = (int)Math.Round(v),
                zeroPad: 3);
            pDrmNum.Stepper = (cur, dir) => StepInList(TE.NumbersInBank(_selLiveDrmBank), cur, dir);
            pDrmNum.OnTypedCommit = v => ToggleTypedDrmRegister((int)Math.Round(v));
            pDrmNum.OnStepCommit = v => StepRegister(_selLiveDrmBank, (int)Math.Round(v));   // krok70: +/- přepne rovnou, bez Enter
            _fLiveDrmNumber = _form.Add(pDrmNum);

            // ---- krok77/78: zkušební pole pro náhled mixpultu - jen Fader/8 kanálových
            // polí jsou SKUTEČNÁ zadávací pole, označení vstupu/matrixu zůstává jen
            // zobrazovaný text (viz DrawMeterBar v RenderWidgetPreview), proto pro ně
            // žádný Parameter/FormField není. ----
            var pDemoFader = Parameter.Number(
                "Fader", "dB", -60, 10, 1, new[] { 0.1, 0.5, 1.0 },
                () => _demoFaderDb,
                v => _demoFaderDb = v,
                showSign: true,
                defaultValue: -6.0);
            _fDemoFader = _form.Add(pDemoFader);

            for (int i = 0; i < 8; i++)
            {
                int idx = i;   // zachytit pro lambdu (jinak by všechna pole sdílela poslední i)
                var pField = Parameter.Number(
                    DemoMixerFieldNames[i], "", -99, 99, 0, new[] { 1.0, 1.0, 10.0 },
                    () => _demoMixerField[idx],
                    v => _demoMixerField[idx] = v,
                    showSign: true,
                    defaultValue: 0.0);
                _fDemoMixerFields[i] = _form.Add(pField);
            }

            // ---- krok79/80: 32 tercie-oktávových (velká tercie, C♯/F/A) pásem
            // ekvalizéru - skutečná zadávací pole ±12dB na pásmo, kmitočet/název tónu
            // u každého pásma zůstává jen zobrazovaný text (viz NoteName/FormatFreqCompact
            // v RenderEqBands). ----
            for (int i = 0; i < EqBandCount; i++)
            {
                int idx = i;   // zachytit pro lambdu (jinak by všechna pole sdílela poslední i)
                var pBand = Parameter.Number(
                    $"Eq{EqBandFreqHz[idx]:0.#}Hz", "dB", -12, 12, 1, new[] { 0.1, 0.5, 1.0 },
                    () => _eqBandGainDb[idx],
                    v => _eqBandGainDb[idx] = v,
                    showSign: true,
                    defaultValue: 0.0);
                _fEqBandGain[idx] = _form.Add(pBand);
            }
        }

        /// <summary>
        /// krok16: jen aktualizuje ukazatel (vybraná banka/výchozí číslo rejstříku v ní) -
        /// BEZ VEDLEJŠÍHO EFEKTU na živé hraní. Používá ToggleTypedRegister při skoku do
        /// jiné banky (tam se o zapnutí rejstříku stará hned potom explicitně
        /// ToggleMelodicRegister - měkké odeznění staré banky, viz tamní komentář).
        /// </summary>
        private void SetSelectedBankPointer(int bank)
        {
            _selBank = bank;
            var nums = TE.NumbersInBank(bank);
            if (nums.Count > 0 && !nums.Contains(_selNumber)) _selNumber = nums[0];
        }

        /// <summary>
        /// krok16/66: změna melodické banky přes pole Banka (šipky i přímé zadání čísla).
        /// Na rozdíl od zadání REJSTŘÍKU (Enter v poli Rej, viz ToggleTypedRegister) se
        /// tohle chová jako tvrdé přepnutí kombinace na varhanách: stará banka se
        /// OKAMŽITĚ umlčí BEZ DOZVUKU (u OPL3 by dozvuk napříč bankami navíc zněl
        /// otřesně) a rovnou se zkusí zapnout STEJNÉ číslo rejstříku v nové bance, pokud
        /// tam existuje - typicky tedy zůstane "klavír" klavírem, jen z jiné banky, beze
        /// změny čísla v poli Rej. Když tam neexistuje, nová banka zůstane prostě tichá,
        /// dokud se nezadá platné číslo.
        /// </summary>
        private void SelectBank(int bank)
        {
            SetSelectedBankPointer(bank);

            var id = new RegisterId(_selBank, _selNumber);
            RegisterId? newId = TE.RegisterExists(id) ? id : null;

            App.OrganEngine?.HardSwitchMelodicBank(newId, DrumBankNumber);
        }

        /// <summary>krok55: totéž pro bicí banku (dnes jen jedna, ale jednou jich může být víc).</summary>
        private void SelectDrmBank(int bank)
        {
            _selLiveDrmBank = bank;
            var nums = TE.NumbersInBank(bank);
            if (nums.Count > 0 && !nums.Contains(_selLiveDrmNumber)) _selLiveDrmNumber = nums[0];
        }

        /// <summary>krok16: další (dir=+1) nebo předchozí (dir=-1) hodnota ze seřazeného seznamu; na kraji zůstane.</summary>
        private static double StepInList(IReadOnlyList<int> list, double current, int dir)
        {
            if (list.Count == 0) return current;
            if (dir > 0)
            {
                foreach (int x in list) if (x > current) return x;
            }
            else
            {
                for (int i = list.Count - 1; i >= 0; i--) if (list[i] < current) return list[i];
            }
            return current;
        }

        /// <summary>
        /// krok16: číslo zadané ručně + Enter přepne rejstřík. Když číslo v aktuální bance neexistuje,
        /// ale existuje v jiné, přepne se i banka (zpětná kompatibilita se starým zadáním jen čísla).
        /// </summary>
        private void ToggleTypedRegister(int number)
        {
            var id = new RegisterId(_selBank, number);
            if (!TE.RegisterExists(id))
            {
                bool found = false;
                foreach (int b in TE.Banks)
                {
                    if (b == DrumBankNumber) continue;   // krok55: melodické pole nepřeskakuje do bicí banky
                    // krok66: tady jen ukazatel (SetSelectedBankPointer), NE SelectBank -
                    // o zapnutí se vzápětí postará ToggleMelodicRegister (měkké odeznění).
                    if (TE.RegisterExists(new RegisterId(b, number))) { SetSelectedBankPointer(b); _selNumber = number; found = true; break; }
                }
                if (!found) return;
                id = new RegisterId(_selBank, number);
            }
            // krok60: tutti/kombinování víc rejstříků zůstává, ale jen v rámci JEDNÉ
            // banky - přepnutím na jinou banku se ty předchozí (z jiné banky) samy
            // vypnou, ať nehraje melodie zbytečně napříč Organ/SF2/OPL3 najednou.
            // Bicí (DrumBankNumber) se tímhle vůbec neřeší - Bic: řádek dál funguje
            // nezávisle (ToggleTypedDrmRegister).
            App.OrganEngine?.ToggleMelodicRegister(id, DrumBankNumber);
        }

        /// <summary>krok55: obdoba ToggleTypedRegister, ale jen pro bicí banku/pole - nemá kam "přeskakovat",
        /// bicích bank je zatím jen jedna.</summary>
        private void ToggleTypedDrmRegister(int number)
        {
            var id = new RegisterId(_selLiveDrmBank, number);
            if (!TE.RegisterExists(id)) return;
            App.OrganEngine?.ToggleRegister(id);
        }

        /// <summary>
        /// krok70: +/- (i kolečko) na poli Rej - ať melodickém, ať bicím - přepne rejstřík
        /// OKAMŽITĚ, bez čekání na Enter: stará banka/rejstřík se vypne a nový se rovnou
        /// zapne (viz AudioEngine.StepRegisterInBank), takže jde rychle "proklikat" celou
        /// banku jedním prstem. Na rozdíl od ToggleTypedRegister/ToggleTypedDrmRegister
        /// (Enter po ručně vypsaném čísle) se tu NEkombinuje/netuttuje - vždycky hraje
        /// nejvýš jeden rejstřík z právě prohledávané banky.
        /// </summary>
        private void StepRegister(int bank, int number)
        {
            var id = new RegisterId(bank, number);
            if (!TE.RegisterExists(id)) return;
            App.OrganEngine?.StepRegisterInBank(id);
        }

        /// <summary>ZMĚNA: Shift = hrubý krok, Ctrl = jemný, jinak normální (klávesy +/- i kolečko).</summary>
        private static StepSize CurrentStepSize()
        {
            var m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Shift) != 0) return StepSize.Coarse;
            if ((m & ModifierKeys.Control) != 0) return StepSize.Fine;
            return StepSize.Normal;
        }

        /// <summary>Klávesy, jejichž znak patří do textového pole (doručí ho TextInput, ne HandleInput).</summary>
        private static bool IsFieldCharKey(Key key)
        {
            return (key >= Key.D0 && key <= Key.D9)
                || (key >= Key.NumPad0 && key <= Key.NumPad9)
                || key == Key.OemPlus || key == Key.OemMinus
                || key == Key.Add || key == Key.Subtract
                || key == Key.OemPeriod || key == Key.OemComma || key == Key.Decimal
                || key == Key.Multiply;
        }

        /// <summary>Doručuje napsané znaky do zaostřeného textového pole.</summary>
        private void Window_TextInput(object sender, TextCompositionEventArgs e)
        {
            if (!_form.HasFocus) return;

            foreach (char c in e.Text)
            {
                _form.HandleChar(c, CurrentStepSize());
            }

            e.Handled = true;
            RenderDashboard();
        }

        private bool EnsureRows(int desiredRows)
        {
            // krok79: horní mez zvednuta z 60 na 200 - samotný 31pásmový ekvalizér
            // (viz WidgetPreviewRows) teď potřebuje přes 50 řádků i bez varhanního
            // panelu/notové osnovy, takže 60 už nestačilo (obsah by se tiše
            // oříznul). 200 dává slušnou rezervu i do budoucna.
            desiredRows = Math.Clamp(desiredRows, HeaderRows, 200);
            if (desiredRows == _lastDesiredRows) return false;
            _lastDesiredRows = desiredRows;

            _grid.SetRows(desiredRows);
            RootCanvas.Width = _grid.Cols * _grid.CellWidth;
            RootCanvas.Height = _grid.Rows * _grid.CellHeight;
            return true;
        }

        private void RenderDashboard()
        {
            string currentFile = _navigator.CurrentFile ?? "No file loaded";
            string fileName = Path.GetFileName(currentFile);
            string folderPath = Path.GetDirectoryName(currentFile) ?? "";
            string fullPath = string.IsNullOrEmpty(folderPath) ? currentFile : $"{folderPath}\\{fileName}";

            bool isActuallyPlaying = _isMidiMode ? !_midiPlayer.IsPaused : _audioPlayer.IsPlaying;

            // ============ Řádek 0: titulek okna + transportní tlačítka + [_] [X] ============
            // (souřadnice tlačítek viz AddTransportButtons a Window_MouseLeftButtonDown)
            // NOVÉ: barva titulku podle toho, zda je okno aktivní (světle modrá),
            // nebo neaktivní (šedá).
            ConsoleColor titleBg = _windowActive ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
            ConsoleColor titleFg = _windowActive ? ConsoleColor.Black : ConsoleColor.Gray;
            ConsoleColor btnFg = _windowActive ? ConsoleColor.DarkBlue : ConsoleColor.Gray;

            _grid.SetCursorPosition(0, 0);
            _grid.BackgroundColor = titleBg;
            _grid.ForegroundColor = titleFg;
            _grid.Write(" ♪♫ PS150 Player".PadRight(TransportCol));

            _grid.ForegroundColor = btnFg;
            _grid.Write("[<<]");
            _grid.Write(" ");

            if (!_isPaused && isActuallyPlaying)
            {
                _grid.BackgroundColor = ConsoleColor.Green;
                _grid.ForegroundColor = ConsoleColor.Black;
                _grid.Write("[►]");
            }
            else
            {
                _grid.ForegroundColor = btnFg;
                _grid.Write("[►]");
            }
            _grid.BackgroundColor = titleBg;
            _grid.Write(" ");

            if (_isPaused || !isActuallyPlaying)
            {
                _grid.BackgroundColor = ConsoleColor.DarkRed;
                _grid.ForegroundColor = ConsoleColor.White;
                _grid.Write("[▄]");
            }
            else
            {
                _grid.ForegroundColor = btnFg;
                _grid.Write("[▄]");
            }
            _grid.BackgroundColor = titleBg;

            _grid.ForegroundColor = btnFg;
            _grid.Write(" [>>]");

            // zbytek titulku modrý až k tlačítkům [_] [X] na pravém okraji
            _grid.ForegroundColor = titleFg;
            _grid.Write(new string(' ', Math.Max(0, Cols - 9 - _grid.CursorX)));
            _grid.Write(" ");
            _grid.ForegroundColor = btnFg;
            _grid.Write("[_]");
            _grid.Write(" ");
            _grid.BackgroundColor = ConsoleColor.DarkRed;
            _grid.ForegroundColor = ConsoleColor.White;
            _grid.Write("[X]");
            _grid.BackgroundColor = titleBg;
            _grid.Write(" ");
            _grid.ResetColor();
            _grid.WriteLine();

            // ============ Řádek 1: cesta a jméno souboru na jednom řádku ============
            // Když se nevejde, zkrátí se začátek cesty (jméno souboru zůstane celé).
            string shown = fullPath;
            int maxLen = Cols - 2;
            if (shown.Length > maxLen)
            {
                shown = "..." + shown.Substring(shown.Length - (maxLen - 3));
            }

            int slash = shown.LastIndexOf('\\');
            string dirPart = slash >= 0 ? shown.Substring(0, slash + 1) : "";
            string namePart = slash >= 0 ? shown.Substring(slash + 1) : shown;

            _grid.SetCursorPosition(0, 1);
            _grid.ForegroundColor = ConsoleColor.Gray;
            _grid.Write(" " + dirPart);
            _grid.ForegroundColor = ConsoleColor.White;
            _grid.Write(namePart);
            _grid.ResetColor();
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            // ============ Řádky 2-5: VU metry L a P, rámované legendou (krok73/74) ============
            // krok72: mocninná (log/exp) stupnice místo dřívější lineární, segmenty
            // kreslené barvou POZADÍ (mezera), ne znakem - viz AudioMeter.RenderMeterCells.
            // krok73: "formulářový" styl - nahoře a dole stejná legenda
            // (MeterBoxTop/MeterBoxBottom), žádný text vlevo/vpravo od pruhu - značka
            // kanálu (L/P) i hodnota v dB jsou napsané PŘÍMO přes segmenty uvnitř
            // pruhu (viz DrawMeterBar). Dva pruhy vedle sebe bez mezery, přesně jako
            // ve skici - při šířce 40 na metr se dva vejdou vedle sebe s rezervou.
            // krok74: mezi horní legendu a pruh přibyl nápovědný řádek s dB stupnicí
            // (MeterBoxTicks) - čistě dekorativní, bez nároku na přesné zarovnání na
            // hranice segmentů (ty jsou dané mocninnou křivkou, ne touhle stupnicí).
            // krok78: žádná mezera úplně vlevo (ať rámeček začíná na sloupci 0) -
            // mezera se místo toho přesunula MEZI kanály L a P, ať se text
            // "-120dBP" rozdělí na čitelné "-120dB P". Jen u tohohle jednoduchého
            // stereo VU metru - u spektrálního analyzéru/mixpultu se mezera
            // nevejde (přesně na těsno, viz RenderWidgetPreview).
            _grid.SetCursorPosition(0, 2);
            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxTop + " " + MeterBoxTop);
            _grid.ResetColor();
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            _grid.SetCursorPosition(0, 3);
            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxTicks + " " + MeterBoxTicks);
            _grid.ResetColor();
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            _grid.SetCursorPosition(0, 4);
            {
                const int meterWidth = 40;

                _grid.ResetColor();
                DrawMeterBar(_leftDb, meterWidth, "L");
                _grid.BackgroundColor = ConsoleColor.Black;
                _grid.Write(" ");
                DrawMeterBar(_rightDb, meterWidth, "P");
                _grid.ResetColor();
            }
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            _grid.SetCursorPosition(0, 5);
            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxBottom + " " + MeterBoxBottom);
            _grid.ResetColor();
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            // ============ Řádek 6: "MIDI (engine), kanálů: N" (jen u .mid souborů) ============
            // krok46: dřív byl tohle "else" větev řádku 2 (VU metry/MIDI info
            // se vzájemně vylučovaly na jednom řádku) - teď má každý svůj
            // vlastní řádek, ať je vidět obojí najednou.
            _grid.SetCursorPosition(0, 6);
            if (_isMidiMode)
            {
                int channelCount;
                lock (_midiNotesLock) { channelCount = _midiUsedChannels.Length; }

                _grid.ForegroundColor = ConsoleColor.DarkGray;
                _grid.Write($" MIDI ({_midiPlayer.OutputDescription}), kanálů: {channelCount}");   // krok20: skutečný zdroj zvuku
                _grid.ResetColor();
            }
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            // ============ Řádek 7: čas, textová pole (hlasitost, bicí, ladění, AJI), stav ============
            // Pole [ ] jsou ovladatelná myší (klik, kolečko), Tab, +/- a zadáním
            // čísla - viz FieldForm v Core. Každé pole se při vykreslení ohlásí
            // formuláři (Place), aby věděl, kam kliknout.
            TimeSpan current = _isMidiMode ? _midiPlayer.CurrentTime : _audioPlayer.CurrentTime;
            TimeSpan total = _isMidiMode ? _midiPlayer.TotalTime : _audioPlayer.TotalTime;
            string timeStr = $"{current:mm\\:ss}/{total:mm\\:ss}";

            _form.BeginFrame();
            _grid.SetCursorPosition(0, 7);
            _grid.ResetColor();

            void Segment(string text, ConsoleColor color)
            {
                _grid.ForegroundColor = color;
                _grid.Write(text);
            }

            void WriteField(FormField field, string label, string suffix)
            {
                _grid.ResetColor();
                Segment(label, ConsoleColor.Cyan);

                _form.Place(field, 3, _grid.CursorX, FieldForm.FieldWidth(field.Parameter));

                switch (_form.StyleOf(field))
                {
                    case FieldStyle.Focused:
                        _grid.BackgroundColor = ConsoleColor.Cyan;
                        _grid.ForegroundColor = ConsoleColor.Black;
                        break;
                    case FieldStyle.Editing:
                        _grid.BackgroundColor = ConsoleColor.Yellow;
                        _grid.ForegroundColor = ConsoleColor.Black;
                        break;
                    case FieldStyle.Error:
                        _grid.BackgroundColor = ConsoleColor.DarkRed;
                        _grid.ForegroundColor = ConsoleColor.White;
                        break;
                    default:
                        _grid.BackgroundColor = ConsoleColor.DarkBlue;
                        _grid.ForegroundColor = ConsoleColor.White;
                        break;
                }

                _grid.Write(_form.Display(field));
                _grid.ResetColor();
                Segment(suffix, ConsoleColor.Cyan);
            }

            Segment($" {timeStr}  ", ConsoleColor.Cyan);
            WriteField(_fVolume, "Vol ", "%  ");

            if (_isMidiMode)
            {
                WriteField(_fDrums, "Bicí ", "  ");
            }

            // krok29: pole "Lad" zrušeno - T a A4 se zobrazují vždy.
            // krok38: na přání zkráceno "T " -> "T" a "st  " -> "  " (ušetří
            // místo na řádku stavového řádku, kam se teď vejde i DrmR).
            // krok71: přepočet na centy (žlutý "+800,0c") úplně zrušen - na
            // formulář 121 znaků se s ním nevešel bicí rejstřík (DrmR).
            WriteField(_fTranspose, "T", "  ");
            WriteField(_fA4, "A4 ", "Hz  ");

            WriteField(_fAji, "AJI ", "  ");

            if (_isMidiMode)
            {
                WriteField(_fMidiBank, "MidB ", "  ");   // krok28: banka/engine pro .mid (netbicí kanály) - nezávislá na živém hraní
                WriteField(_fMelodicRegister, "UniR ", "  ");   // krok31: univerzální tónový rejstřík (0 = skutečné nástroje ze souboru)
                WriteField(_fDrumBank, "DrmB ", "  ");   // krok30: totéž, jen pro kanál D10 (bicí)
                WriteField(_fDrumRegister, "DrmR ", "  ");   // krok37: bicí sada/kit (0 = skutečná sada ze souboru)
            }

            if (_isMidiMode && _midiPlayer.AjiEnabled)
            {
                string ajiText = _midiPlayer.AjiAvailable
                    ? $"{PS150.Core.AjiPlanner.TemperamentLabel}: {_midiPlayer.AjiStatusText}"
                    : "bez analyzy";
                Segment(ajiText + "  ", ConsoleColor.Cyan);
            }

            if (!_isMidiMode && _audioPlayer.UsesNetwork)
            {
                Segment($"Vystup: ASRock {_audioPlayer.NetworkLatencyMs}ms  ", ConsoleColor.Green);
            }

            if (_form.Error != null)
            {
                Segment($"! {_form.Error}  ", ConsoleColor.Red);
            }
            else if (_form.HasFocus)
            {
                Segment("Tab=další  *=výchozí  Esc=pryč", ConsoleColor.DarkGray);   // ZMĚNA: krok už se netrvale nevolí
            }

            _grid.ResetColor();
            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();
        }

        /// <summary>
        /// Sloučené vykreslení "stavové oblasti" pod cestou k souboru -
        /// dřív to byly dvě vzájemně se vylučující metody (RenderMetersOnly
        /// vs. RenderMidiStaffOnly), teď se skládá ze tří nezávislých bloků:
        ///
        ///  - L/R (zvukovka)     - jen když hraje soubor přes AudioPlayer (!_isMidiMode)
        ///  - H/S/V + Reg:/Akt:  - vždy, když je připojené MIDI-IN (bez ohledu na soubor)
        ///  - notová osnova      - jen u .mid souborů (_isMidiMode)
        ///
        /// Klaviatura na MIDI-IN totiž může hrát kdykoliv, bez ohledu na to,
        /// co se zrovna prohlíží/přehrává - varhanní panel na tom tedy nesmí
        /// záviset.
        /// </summary>
        /// <summary>
        /// NOVÉ: formulář MIDI vstupů. Jedno rozhraní = jeden řádek: vlevo stručné "MIDI"
        /// (při víc rozhraních MIDI1, MIDI2...), hned za ním poslední události, nejnovější
        /// úplně vpravo, starší se posouvají doleva. Odpojené rozhraní zešedne.
        /// Zápis: 1:C4 100 = kanál 1, nota C4 stisknuta rychlostí 100; 1:C4 - = puštěna;
        /// 10:36 100 = bicí (kanál 10 píše číslo noty); 1:CC64=127 = ovladač (pedál);
        /// 1:PB+512 = pitch bend; 1:PG5 = změna programu.
        /// </summary>
        private void RenderMidiPortRows(MidiPortSnapshot[] ports)
        {
            const int labelWidth = 7;
            int eventsWidth = Cols - labelWidth;

            foreach (var port in ports)
            {
                bool on = port.Connected;

                _grid.ForegroundColor = on ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                _grid.Write(TextLayout.FitWidth(ports.Length > 1 ? $" MIDI{port.Index + 1}" : " MIDI", labelWidth));

                // Co se vejde zprava: nejnovější událost je poslední, mezi událostmi dvě mezery.
                var shown = new List<string>();
                int used = 0;
                for (int i = port.Recent.Length - 1; i >= 0; i--)
                {
                    int need = port.Recent[i].Length + (shown.Count > 0 ? 2 : 0);
                    if (used + need > eventsWidth - 1) break;
                    shown.Insert(0, port.Recent[i]);
                    used += need;
                }

                string newest = shown.Count > 0 ? shown[shown.Count - 1] : "";
                string older = shown.Count > 1 ? string.Join("  ", shown.Take(shown.Count - 1)) + "  " : "";
                string pad = new string(' ', Math.Max(0, eventsWidth - older.Length - newest.Length));

                _grid.ForegroundColor = on ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray;
                _grid.Write(pad + older);
                _grid.ForegroundColor = on ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
                _grid.Write(newest);
                _grid.ResetColor();
                _grid.WriteLine();
            }
        }

        /// <summary>krok16: vykreslí textové pole formuláře na zadaný řádek (stejné barvy jako pole v řádku 3).</summary>
        private void WriteFormField(int row, FormField field, string label, string suffix)
        {
            _grid.ResetColor();
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(label);

            _form.Place(field, row, _grid.CursorX, FieldForm.FieldWidth(field.Parameter));

            switch (_form.StyleOf(field))
            {
                case FieldStyle.Focused:
                    _grid.BackgroundColor = ConsoleColor.Cyan;
                    _grid.ForegroundColor = ConsoleColor.Black;
                    break;
                case FieldStyle.Editing:
                    _grid.BackgroundColor = ConsoleColor.Yellow;
                    _grid.ForegroundColor = ConsoleColor.Black;
                    break;
                case FieldStyle.Error:
                    _grid.BackgroundColor = ConsoleColor.DarkRed;
                    _grid.ForegroundColor = ConsoleColor.White;
                    break;
                default:
                    _grid.BackgroundColor = ConsoleColor.DarkBlue;
                    _grid.ForegroundColor = ConsoleColor.White;
                    break;
            }

            _grid.Write(_form.Display(field));
            _grid.ResetColor();
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(suffix);
        }

        // krok55: dřív jeden řádek navíc nad notovými osnovami, pořád vidět, jakmile bylo
        // připojené MIDI-IN. Teď je to samostatný blok POD osnovami, oddělený dvěma
        // prázdnými řádky, a vidět je jen když přehrávač STOJÍ (nehraje) - při poslechu by
        // jinak splýval s osnovami a kradl místo. Klaviatuře/pedálům samým je to jedno,
        // InputManager a ToneEngine běží pořád bez ohledu na to, co je vidět na obrazovce -
        // na definitivním HW (bez přehrávače písniček) bude tenhle blok vidět furt.
        private const int OrganBlankRows = 2;

        // krok67: řádek s ladění pro živé hraní (T/A4) - vždycky přesně jeden řádek.
        private const int LiveTuningRows = 1;

        /// <summary>
        /// krok67: ladění/transpozice pro ŽIVÉ hraní - vlastní řádek hned pod MIDI
        /// vstupy a nad Mel:/Bic:, nezávislý na ladění přehrávače .mid (to je v
        /// záhlaví, _fTranspose/_fA4). Platí jen pro ToneEngine (viz komentář u
        /// ToneEngine.LiveTransposeSemitones) - banky OPL3/GM (8/2) se jím zatím
        /// neřídí.
        /// </summary>
        private void RenderLiveTuningRow()
        {
            int row = _grid.CursorY;
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(" Lad: ");
            WriteFormField(row, _fLiveTranspose, "T", "  ");
            WriteFormField(row, _fLiveA4, "A4 ", "Hz  ");
            _grid.ResetColor();
            _grid.WriteLine();
        }

        /// <summary>krok69: kolik řádků AJI pro živé hraní zrovna zabírá - druhý (parametry AutoDetect) jen když je zrovna zvolený ten režim.</summary>
        private int LiveAjiRowCount() =>
            (App.OrganEngine?.LiveAji.Mode ?? PS150.Core.ToneEngine.LiveAjiMode.Off) == PS150.Core.ToneEngine.LiveAjiMode.AutoDetect ? 2 : 1;

        /// <summary>
        /// krok69: AJI pro ŽIVÉ hraní - jeden řádek vždycky (mód, ladění, tónika,
        /// dur/moll, aktuálně platná tónina), druhý (parametry AutoDetect - okno,
        /// krok, práh, minimální počet kroků) jen když je zrovna zvolený režim
        /// Auto (viz LiveAjiMode.AutoDetect) - u Pevné tóniny/Vypnuto by jen
        /// zabíral místo bez užitku.
        /// </summary>
        private void RenderLiveAjiRows()
        {
            var liveAji = App.OrganEngine?.LiveAji;
            var mode = liveAji?.Mode ?? PS150.Core.ToneEngine.LiveAjiMode.Off;

            int row = _grid.CursorY;
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(" AJI: ");
            WriteFormField(row, _fLiveAjiMode, "Mod", "  ");
            WriteNameTag(LiveAjiModeNames, (int)mode);

            WriteFormField(row, _fLiveAjiTemperament, "Lad", "  ");
            WriteNameTag(LiveAjiTemperamentNames, (int)(liveAji?.Temperament ?? PS150.Core.AjiTemperament.JustIntonation5Limit));

            WriteFormField(row, _fLiveAjiTonic, "Ton", " ");
            WriteNameTag(LiveAjiTonicNames, liveAji?.FixedTonicPc ?? 0);
            _grid.Write(" ");
            WriteFormField(row, _fLiveAjiMinor, "Moll", "  ");

            if (mode != PS150.Core.ToneEngine.LiveAjiMode.Off)
            {
                string status = App.OrganEngine?.LiveAjiStatusText() ?? "";
                if (!string.IsNullOrEmpty(status))
                {
                    _grid.ForegroundColor = ConsoleColor.Yellow;
                    _grid.Write($"= {status}  ");
                }
            }

            _grid.ResetColor();
            _grid.WriteLine();

            if (mode == PS150.Core.ToneEngine.LiveAjiMode.AutoDetect)
            {
                int row2 = _grid.CursorY;
                _grid.ForegroundColor = ConsoleColor.Cyan;
                _grid.Write("      ");
                WriteFormField(row2, _fLiveAjiWindow, "Okno ", "s  ");
                WriteFormField(row2, _fLiveAjiHop, "Krok ", "s  ");
                WriteFormField(row2, _fLiveAjiMargin, "Prah ", "  ");
                WriteFormField(row2, _fLiveAjiMinHops, "MinKr ", "  ");
                _grid.ResetColor();
                _grid.WriteLine();
            }
        }

        /// <summary>krok69: krátký, šedě napsaný popisek hned za číselným polem (mód/ladění/tónika) - ať není vidět jen holé číslo.</summary>
        private void WriteNameTag(string[] names, int index)
        {
            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(index >= 0 && index < names.Length ? names[index] : "?");
            _grid.ResetColor();
        }

        /// <summary>krok73/74: zkušební "formulářová" legenda nad/pod/uvnitř VU metru - stejná
        /// šířka jako DrawMeterBar (40 znaků), beze změny barvy (DarkGray nastavuje volající).
        /// Má to být jednotný styl i pro budoucí spektrální analyzér/ekvalizér/mixpult.
        /// MeterBoxTicks je čistě dekorativní nápověda dB stupnice, NENÍ přesně zarovnaná
        /// na hranice segmentů (ty řídí mocninná křivka, viz AudioMeter.RenderMeterCells) -
        /// doplněno doprava mezerami na plnou šířku 40 znaků.</summary>
        private const string MeterBoxTop = "┌───────┬───────┬───────┬───────┬──────┐";
        private const string MeterBoxBottom = "└───────┴───────┴───────┴───────┴──────┘";
        private const string MeterBoxTicks = "-80     -40     -20     -10     -5     0";

        /// <summary>
        /// krok72/73/74: vykreslí jeden pruh VU metru šířky <paramref name="width"/> na
        /// aktuální pozici kurzoru. Segmenty = barva POZADÍ (mezera): tmavě zelená pro
        /// normální rozsah (krok74 - původní jasně zelená "přebíjela" bílý text), červená
        /// (jasná, ať pořádně "svítí") pro limitaci/nejsilnější hodnoty (viz
        /// AudioMeter.RenderMeterCells), černá, kde nesvítí. krok74: značka kanálu
        /// (<paramref name="channelLabel"/>, např. "L"/"P") je úplně nalevo v pruhu,
        /// hodnota v dB zůstává zarovnaná doprava (nejblíž limitaci, kde je nejdůležitější) -
        /// obojí bílým písmem, podkladová barva segmentu pod písmeny zůstává beze změny.
        /// Žádný text se nepíše mimo tenhle pruh (ani před, ani za).
        /// </summary>
        private void DrawMeterBar(double db, int width, string channelLabel)
        {
            var cells = PS150.Core.AudioMeter.RenderMeterCells(db, width);
            string valueText = $"{db,6:F1}dB";
            int valueStart = Math.Max(0, width - valueText.Length);

            for (int i = 0; i < width; i++)
            {
                _grid.BackgroundColor = cells[i] switch
                {
                    PS150.Core.MeterCellColor.Hot => ConsoleColor.Red,
                    PS150.Core.MeterCellColor.Normal => ConsoleColor.DarkGreen,
                    _ => ConsoleColor.Black
                };

                if (i < channelLabel.Length)
                {
                    _grid.ForegroundColor = ConsoleColor.White;
                    _grid.Write(channelLabel[i].ToString());
                }
                else if (i >= valueStart && i - valueStart < valueText.Length)
                {
                    _grid.ForegroundColor = ConsoleColor.White;
                    _grid.Write(valueText[i - valueStart].ToString());
                }
                else
                {
                    _grid.Write(" ");
                }
            }
        }

        // ------------------------------------------------------------------
        // krok76: zkušební "formulářová" skořápka pro spektrální analyzér/
        // ekvalizér a mixpult - stejný grafický jazyk jako VU metr (legenda
        // nahoře/dole, hodnota přímo v pruhu), zatím BEZ reálných dat (žádná
        // FFT, žádné skutečné EQ pásmo, žádný model kanálů mixpultu - to
        // všechno je samostatný, mnohem větší krok). Jde jen o vizuální
        // náhled jednoho příkladového pásma / jednoho příkladového kanálu.
        // ------------------------------------------------------------------

        private const string EqBoxTop = "┌────────┬───────┐";
        private const string EqBoxBottom = "└────────┴───────┘";
        private const string EqBoxTicks = "-12      0     +12";

        private const string FaderBoxTicks = "-∞      -40     -20     -10     0    +10";

        private const string FieldsBoxTop = "┌────┬────┬────┬────┬────┬────┬────┬────┐";
        private const string FieldsBoxBottom = "└────┴────┴────┴────┴────┴────┴────┴────┘";
        private const string FieldsBoxLabels = "Gain Pan  Gate Comp HPF  E.Ga E.Fr E.Q   ";

        /// <summary>
        /// krok79/80: 32 pásem, zobrazená po osmi (viz RenderEqBands) - 1 prázdný řádek +
        /// 1 popisek + horní rámeček + horní stupnice + 32 řádků pásem + stupnice po
        /// každé ze 4 skupin (8+8+8+8) + dolní rámeček.
        /// = 2 + 2 + EqBandCount + 4 + 1
        /// </summary>
        private const int EqBandsBlockRows = 2 + 2 + EqBandCount + 4 + 1;

        /// <summary>Počet řádků, které si ukázka mixpultu zabere - viz RenderWidgetPreview.
        /// 1 prázdný řádek + 1 popisek + 4 (horní legenda/stupnice/pruh/dolní legenda).</summary>
        private const int MixerBlockRows = 1 + 1 + 4;

        private const int WidgetPreviewRows = EqBandsBlockRows + MixerBlockRows;

        private static readonly string[] NoteNamesSharp =
            { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" };

        /// <summary>
        /// krok79/80/81: název nejbližšího tónu v temperované řadě podle AKTUÁLNÍHO
        /// ladění (_tuning.ReferenceA4Hz) - počítá se s plnou chromatickou řadou (12
        /// půltónů), aby zaokrouhlení na nejbližší půltón bylo přesné. krok80: křížek
        /// se zobrazuje normálně (C♯ zůstává "C♯") - místa ve formuláři je dost.
        /// krok81: u not BEZ křížku (A, F...) je na jeho místě mezera, ne nic - tím
        /// pádem je písmeno+znak(křížek/mezera) vždy přesně 2 znaky a oktáva (a po ní
        /// čárka s kmitočtem v RenderEqBands) drží stejný sloupec napříč všemi pásmy,
        /// místo aby "poskakovala" podle toho, jestli má tón křížek nebo ne. Jedinou
        /// výjimkou je dvojciferná desátá oktáva (C♯10/F10) - tam se sloupec posune,
        /// to už se zarovnat nedá.  Oktáva ve vědecké notaci (A4 = komorní A = 440Hz,
        /// střední C = C4).
        /// </summary>
        private string NoteName(double freqHz)
        {
            if (freqHz <= 0.0) return "--";
            double a4 = _tuning.ReferenceA4Hz;
            int semitoneFromA4 = (int)Math.Round(12.0 * Math.Log2(freqHz / a4));
            int midi = 69 + semitoneFromA4;
            int noteIndex = ((midi % 12) + 12) % 12;
            int octave = (midi / 12) - 1;
            string full = NoteNamesSharp[noteIndex];
            string letter = full.Substring(0, 1);
            string sharpOrSpace = full.Length > 1 ? "♯" : " ";   // krok81: mezera místo chybějícího křížku, ať sloupec drží
            return $"{letter}{sharpOrSpace}{octave}";
        }

        /// <summary>
        /// krok79: hodně zhuštěný zápis kmitočtu - pod 1000Hz celé číslo v Hz ("350"),
        /// od 1000Hz výš v kHz ("2,2k"/"17k"), nad 10kHz bez desetinného místa, ať se
        /// text vejde vedle názvu tónu. Desetinný oddělovač jde přes výchozí formátování
        /// (stejně jako dB hodnota v DrawMeterBar), takže na českém Windows vyjde čárka.
        /// </summary>
        private static string FormatFreqCompact(double freqHz)
        {
            if (freqHz < 1000.0) return $"{freqHz:0}";
            double khz = freqHz / 1000.0;
            return khz < 10.0 ? $"{khz:0.0}k" : $"{khz:0}k";
        }

        /// <summary>
        /// krok77: společná část pro zaostřené/editované/chybné zadávací pole přes
        /// libovolně široký pruh (metr/fader/úzké mixpultové pole) - barva FieldStyle
        /// (azurová/žlutá/tmavě červená) PŘEBIJE barvu celého úseku, text je živý
        /// obsah pole (_form.Display - se závorkami a podtržítkem při psaní), ať je
        /// jasně vidět, že se do něj dá psát. Zarovnáno doprava, oříznuté zleva,
        /// pokud se nevejde.
        /// </summary>
        private void WriteFocusedFieldOverlay(FormField field, int width, FieldStyle style)
        {
            _grid.BackgroundColor = style switch
            {
                FieldStyle.Editing => ConsoleColor.Yellow,
                FieldStyle.Error => ConsoleColor.DarkRed,
                _ => ConsoleColor.Cyan
            };
            _grid.ForegroundColor = ConsoleColor.Black;

            string text = _form.Display(field);
            if (text.Length > width) text = text.Substring(text.Length - width);
            _grid.Write(text.PadLeft(width));
        }

        /// <summary>
        /// krok76/77: lineární pruh tahového potenciometru/faderu (ekvalizér ±12dB,
        /// kanálový fader...), celý je ZAOSTŘITELNÉ pole. Na rozdíl od DrawMeterBar
        /// (mocninná křivka, tři barvy včetně červené pro limitaci) je tu jen "ujetá
        /// dráha" (šedě) od levého okraje po aktuální hodnotu a zbytek dráhy ČERNĚ
        /// (krok77: ne červeně jako dřív - fader/EQ smí jít do přebuzení, není to
        /// chyba, takže žádné výstražné červené zvýraznění). Při zaostření/editaci
        /// barva FieldStyle přebije celý pruh - viz WriteFocusedFieldOverlay.
        /// </summary>
        private void DrawFaderBarField(FormField field, int width, double minValue, double maxValue, string unit)
        {
            FieldStyle style = _form.StyleOf(field);
            _form.Place(field, _grid.CursorY, _grid.CursorX, width);

            if (style != FieldStyle.Normal)
            {
                WriteFocusedFieldOverlay(field, width, style);
                return;
            }

            var filled = PS150.Core.AudioMeter.RenderFaderFill(field.Parameter.Value, width, minValue, maxValue);
            string valueText = field.Parameter.FormatValue() + unit;
            int valueStart = Math.Max(0, width - valueText.Length);

            for (int i = 0; i < width; i++)
            {
                _grid.BackgroundColor = filled[i] ? ConsoleColor.DarkGray : ConsoleColor.Black;

                if (i >= valueStart && i - valueStart < valueText.Length)
                {
                    _grid.ForegroundColor = ConsoleColor.White;
                    _grid.Write(valueText[i - valueStart].ToString());
                }
                else
                {
                    _grid.Write(" ");
                }
            }
        }

        /// <summary>
        /// krok77: jedno úzké zadávací pole v "nahuštěném" pruhu mixpultu (Gain/Pan/
        /// Gate/Comp/HPF/E.Ga/E.Fr/E.Q) - v klidu bílé číslo na černém pozadí, při
        /// zaostření/editaci barva FieldStyle přes celou šířku (viz WriteFocusedFieldOverlay).
        /// </summary>
        private void DrawTinyField(FormField field, int width, string valueTextNormal)
        {
            FieldStyle style = _form.StyleOf(field);
            _form.Place(field, _grid.CursorY, _grid.CursorX, width);

            if (style != FieldStyle.Normal)
            {
                WriteFocusedFieldOverlay(field, width, style);
                return;
            }

            _grid.BackgroundColor = ConsoleColor.Black;
            _grid.ForegroundColor = ConsoleColor.White;
            string text = valueTextNormal.Length > width ? valueTextNormal.Substring(valueTextNormal.Length - width) : valueTextNormal;
            _grid.Write(text.PadLeft(width));
        }

        /// <summary>
        /// krok79/80/82: vykreslí celý 32pásmový tercie-oktávový ekvalizér (velká
        /// tercie, C♯/F/A - kmitočty z PS150.Core.Equalizers.GraphicEqBands, viz
        /// tam) na aktuální pozici kurzoru, zobrazený
        /// "po osmi" (8+8+8+8 = 32 - krok80: čtyři stejné skupiny, dřív 8+8+8+7),
        /// aby se dalo při čtení dolů snadno zkontrolovat, kde je 0dB - po každé
        /// skupině osmi pásem se zopakuje samotná stupnice (bez rámečku, ten je jen
        /// nahoře a dole). Pásma jsou stále jen vizuální ukázka (hodnota metru = L
        /// kanál skutečného VU metru, ať to žije), ale zisk každého pásma je SKUTEČNÉ
        /// zadávací pole (_fEqBandGain). Vlevo u každého pásma je místo "100Hz" teď
        /// název nejbližšího tónu (křížek se krok80 zobrazuje normálně) + hodně
        /// zhuštěná frekvence (viz NoteName/FormatFreqCompact), např. "F4,349"
        /// nebo "C♯10,18k".
        /// </summary>
        private void RenderEqBands()
        {
            _grid.WriteLine();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.WriteLine(" Ekvalizér, 32 tercie-oktávových pásem (velká tercie C♯/F/A, 17Hz-22kHz), se zadávacími poli:");
            _grid.ResetColor();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxTop + EqBoxTop);
            _grid.ResetColor();
            _grid.WriteLine();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxTicks + EqBoxTicks);
            _grid.ResetColor();
            _grid.WriteLine();

            int[] groupSizes = { 8, 8, 8, 8 };   // 8+8+8+8 = 32 = EqBandCount
            int band = 0;
            for (int g = 0; g < groupSizes.Length; g++)
            {
                for (int k = 0; k < groupSizes[g]; k++)
                {
                    double freq = EqBandFreqHz[band];
                    string label = NoteName(freq) + "," + FormatFreqCompact(freq);
                    DrawMeterBar(_leftDb, 40, label);
                    DrawFaderBarField(_fEqBandGain[band], 18, -12.0, 12.0, "dB");
                    _grid.ResetColor();
                    _grid.WriteLine();
                    band++;
                }

                // krok79: po každé skupině (i po té poslední, jako "závěrečná" legenda
                // těsně před dolním rámečkem) zopakovat samotnou stupnici, bez rámečku.
                _grid.ForegroundColor = ConsoleColor.DarkGray;
                _grid.Write(MeterBoxTicks + EqBoxTicks);
                _grid.ResetColor();
                _grid.WriteLine();
            }

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxBottom + EqBoxBottom);
            _grid.ResetColor();
            _grid.WriteLine();
        }

        /// <summary>
        /// krok76/77: vykreslí ukázkový mixpultový kanál na aktuální pozici kurzoru -
        /// čistě pro vizuální kontrolu stylu a ovládání, hodnoty jsou z části
        /// vymyšlené/odvozené z L kanálu VU metru, ať to v náhledu žije. Fader/8 polí
        /// mixpultu jsou SKUTEČNÁ zadávací pole (klik/kolečko/+-/zadání čísla),
        /// označení vstupu/matrixu ("In1") zůstává jen zobrazovaný text.
        /// </summary>
        private void RenderWidgetPreview()
        {
            RenderEqBands();

            _grid.WriteLine();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.WriteLine(" Mixážní pult (ukázka jednoho kanálu, přesně 121 znaků, se zadávacími poli):");
            _grid.ResetColor();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxTop + MeterBoxTop + FieldsBoxTop);
            _grid.ResetColor();
            _grid.WriteLine();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxTicks + FaderBoxTicks + FieldsBoxLabels);
            _grid.ResetColor();
            _grid.WriteLine();

            DrawMeterBar(_leftDb, 40, $"In{_demoMixerInput:F0}");   // krok78: označení vstupu/matrixu je jen zobrazovaný text, ne pole
            DrawFaderBarField(_fDemoFader, 40, -60.0, 10.0, "dB");

            _grid.BackgroundColor = ConsoleColor.Black;
            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write("│");
            for (int i = 0; i < 8; i++)
            {
                DrawTinyField(_fDemoMixerFields[i], 4, _fDemoMixerFields[i].Parameter.FormatValue());
                _grid.BackgroundColor = ConsoleColor.Black;
                _grid.ForegroundColor = ConsoleColor.DarkGray;
                _grid.Write("│");
            }
            _grid.ResetColor();
            _grid.WriteLine();

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.Write(MeterBoxBottom + MeterBoxBottom + FieldsBoxBottom);
            _grid.ResetColor();
            _grid.WriteLine();
        }

        private void RenderStatusArea()
        {
            bool midiConnected = App.Input?.IsLiveDeviceConnected ?? false;
            bool isActuallyPlaying = _isMidiMode ? !_midiPlayer.IsPaused : _audioPlayer.IsPlaying;
            bool showOrganPanel = midiConnected && !isActuallyPlaying;

            int[] usedChannels = Array.Empty<int>();
            (int Channel, int Note, double BendCents)[] activeNotesSnapshot = Array.Empty<(int, int, double)>();
            string? errorMessage = null;
            List<string>? errorLines = null;
            string? seekDiagnostic = null;
            int staffRows = 0;

            if (_isMidiMode)
            {
                lock (_midiNotesLock)
                {
                    usedChannels = _midiUsedChannels;
                    activeNotesSnapshot = _midiActiveNotes
                        .Select(kv => (Channel: kv.Key.Channel, Note: kv.Key.Note, BendCents: kv.Value))
                        .ToArray();
                    errorMessage = _midiErrorMessage;
                }

                if (errorMessage != null)
                {
                    errorLines = TextLayout.WrapPath(errorMessage, Cols - 1);
                    staffRows = 1 + errorLines.Count;
                }
                else
                {
                    seekDiagnostic = _midiPlayer.LastSeekDiagnostic;
                    staffRows = Math.Max((usedChannels.Length + StaffColumns - 1) / StaffColumns, 1) + (string.IsNullOrEmpty(seekDiagnostic) ? 0 : 1);
                }
            }

            // Čas, hlasitost, ladění, AJI a výstup jsou už v řádku 3 záhlaví
            // (viz RenderDashboard, jsou to textová pole).
            // krok55: formulář MIDI vstupů (jeden řádek na každé rozhraní, i odpojené, šedě)
            // se počítá/kreslí jen když se vůbec ukazuje živý panel.
            MidiPortSnapshot[] midiPorts = showOrganPanel ? (App.Input?.GetPorts() ?? Array.Empty<MidiPortSnapshot>()) : Array.Empty<MidiPortSnapshot>();

            // krok56: 2 prázdné řádky + MIDI vstupy + (Mel a Bic už nejsou nutně
            // jen 1 řádek - za polem Rej je navíc seznam všech rejstříků dané
            // banky, zalomený, takže se může rozlít na víc řádků - viz WrapRegisterNames).
            int melLines = showOrganPanel ? WrapRegisterNames(_selBank, RegRowStartCol(" Mel: ", _fRegBank, _fRegNumber)).Count : 0;
            int drmLines = showOrganPanel ? WrapRegisterNames(_selLiveDrmBank, RegRowStartCol(" Bic: ", _fLiveDrmBank, _fLiveDrmNumber)).Count : 0;
            int organRows = showOrganPanel ? (OrganBlankRows + midiPorts.Length + LiveTuningRows + LiveAjiRowCount() + melLines + drmLines) : 0;
            int totalRows = staffRows + organRows + WidgetPreviewRows;   // krok76: zkušební náhled dole
            if (EnsureRows(HeaderRows + totalRows))
            {
                RenderDashboard();
            }

            _grid.SetCursorPosition(0, HeaderRows);

            // ---- notové osnovy (jen u .mid, hned pod přehrávačem) ----
            if (_isMidiMode)
            {
                if (errorMessage != null)
                {
                    _grid.ForegroundColor = ConsoleColor.Red;
                    _grid.WriteLine(TextLayout.FitWidth(" CHYBA přehrávání:", Cols));
                    foreach (string line in errorLines!)
                    {
                        _grid.WriteLine(TextLayout.FitWidth($" {line}", Cols));
                    }
                    _grid.ResetColor();
                }
                else
                {
                    var notesByChannel = activeNotesSnapshot
                        .GroupBy(n => n.Channel)
                        .ToDictionary(
                            g => g.Key,
                            g => g.OrderBy(n => n.Note).ToArray());

                    // NOVÉ: osnovy kanálů jsou po čtveřicích vedle sebe v jednom řádku
                    // (121 sloupců = 4 x 30 + 1), každá čtvrtina má štítek kanálu a noty.
                    int cellWidth = Cols / StaffColumns;   // 30

                    for (int k = 0; k < usedChannels.Length; k++)
                    {
                        int channel = usedChannels[k];
                        string channelLabel = channel == 9 ? "D10" : $"C{channel + 1:D2}";
                        bool isDrumChannel = channel == 9;
                        string notes = notesByChannel.TryGetValue(channel, out var noteEntries)
                            ? string.Join(" ", noteEntries.Select(n => isDrumChannel
                                ? NoteDisplay.DrumAbbreviation(n.Note)
                                : NoteDisplay.NoteName(n.Note, n.BendCents, _midiPlayer.KeyPrefersFlats)))
                            : "";

                        string label = $" {channelLabel}:";
                        _grid.ForegroundColor = ConsoleColor.White;
                        _grid.Write(label);
                        _grid.ForegroundColor = ConsoleColor.Green;
                        _grid.Write(TextLayout.FitWidth(notes, cellWidth - label.Length));
                        _grid.ResetColor();

                        if (k % StaffColumns == StaffColumns - 1 || k == usedChannels.Length - 1)
                        {
                            _grid.WriteLine();
                        }
                    }

                    if (usedChannels.Length == 0)
                    {
                        _grid.WriteLine(TextLayout.FitWidth(" (osnovy zatím nerozpoznané)", Cols));
                    }

                    if (!string.IsNullOrEmpty(seekDiagnostic))
                    {
                        _grid.ForegroundColor = ConsoleColor.Yellow;
                        _grid.WriteLine(TextLayout.FitWidth($" [Seek] {seekDiagnostic}", Cols));
                        _grid.ResetColor();
                    }
                }
            }

            // ---- krok55: živé hraní - jen když přehrávač stojí a je připojené MIDI-IN ----
            if (showOrganPanel)
            {
                for (int i = 0; i < OrganBlankRows; i++) _grid.WriteLine();

                RenderMidiPortRows(midiPorts);

                RenderLiveTuningRow();
                RenderLiveAjiRows();

                RenderRegRow(" Mel: ", _fRegBank, _fRegNumber, _selBank);
                RenderRegRow(" Bic: ", _fLiveDrmBank, _fLiveDrmNumber, _selLiveDrmBank);
            }

            RenderWidgetPreview();   // krok76: zkušební náhled spektra/EQ/mixpultu
        }

        /// <summary>
        /// krok56: sloupec, na kterém končí pole Banka a Rej (a tedy kde začíná seznam
        /// rejstříků za nimi) - čistě z šířek polí, BEZ kreslení. Potřeba předem, aby
        /// šlo spočítat kolik řádků seznam zabere (viz RenderStatusArea), dřív než se
        /// vůbec začne kreslit (EnsureRows musí znát výšku předem).
        /// </summary>
        private static int RegRowStartCol(string label, FormField bankField, FormField numberField)
        {
            return label.Length
                + "Banka ".Length + FieldForm.FieldWidth(bankField.Parameter) + 2
                + "Rej ".Length + FieldForm.FieldWidth(numberField.Parameter) + 2;
        }

        /// <summary>
        /// krok56: jeden řádek "Banka/Rej + seznam všech rejstříků dané banky" - zápis
        /// čísla do Rej a Enter rejstřík přepne (ToggleTypedRegister/Drm, beze změny),
        /// a jestli je zapnutý se pozná podle zeleného podbarvení přímo v tomhle
        /// seznamu - samostatné pole Zap i řádek "Akt:" tím padly, je to tak kratší.
        /// </summary>
        private void RenderRegRow(string label, FormField bankField, FormField numberField, int bank)
        {
            int regRow = _grid.CursorY;
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(label);
            WriteFormField(regRow, bankField, "Banka ", "  ");
            WriteFormField(regRow, numberField, "Rej ", "  ");

            foreach (var line in WrapRegisterNames(bank, _grid.CursorX))
            {
                foreach (var (text, active) in line)
                {
                    _grid.BackgroundColor = active ? ConsoleColor.DarkGreen : ConsoleColor.Black;
                    _grid.ForegroundColor = active ? ConsoleColor.White : ConsoleColor.Gray;
                    _grid.Write(text);
                }
                _grid.ResetColor();
                _grid.WriteLine();
            }
        }

        /// <summary>
        /// krok56: jména všech rejstříků dané banky (TE.NumbersInBank/TE.RegisterName,
        /// tedy přesně to, co je zapsané ve var ...Presets = new VoicePreset[] ... v
        /// BuildRegistry()), oddělená mezerou a zalomená na šířku řádku (Cols). Vrací
        /// řádky jako pole dvojic (text, je-aktivní) - text už obsahuje oddělující
        /// mezeru před sebou, pokud na řádku není první. firstLineStartCol = sloupec,
        /// kde na PRVNÍM řádku seznam začíná (hned za polem Rej); další řádky (pokud
        /// se nevejde) začínají od levého kraje.
        /// </summary>
        private List<(string Text, bool Active)[]> WrapRegisterNames(int bank, int firstLineStartCol)
        {
            var lines = new List<(string, bool)[]>();
            var current = new List<(string, bool)>();
            int col = firstLineStartCol;
            bool lineHasItem = false;

            foreach (int number in TE.NumbersInBank(bank))
            {
                // krok57: číslo rejstříku natvrdo před jménem ("001 Grand Piano"), ať se
                // dá vyčíst, co napsat do pole Rej, místo hádání/pokusu-omylu.
                var id = new RegisterId(bank, number);
                string name = $"{number:000} {TE.RegisterName(id) ?? "?"}";
                bool active = App.OrganEngine?.IsRegisterActive(id) ?? false;

                string piece = lineHasItem ? " " + name : name;
                if (lineHasItem && col + piece.Length > Cols)
                {
                    lines.Add(current.ToArray());
                    current = new List<(string, bool)>();
                    col = 0;
                    lineHasItem = false;
                    piece = name;
                }

                current.Add((piece, active));
                col += piece.Length;
                lineHasItem = true;
            }

            lines.Add(current.ToArray());   // i prázdný poslední řádek, když banka nemá žádný rejstřík
            return lines;
        }

    }
}
