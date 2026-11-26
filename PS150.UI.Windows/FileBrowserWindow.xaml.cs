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

        // Záhlaví má pevné čtyři řádky:
        //   0 titulek okna (vlastní) + transportní tlačítka + [_] [X]
        //   1 cesta a jméno souboru (jeden řádek, bez zalomení)
        //   2 VU metry L a P (u MIDI přehled)
        //   3 čas, hlasitost, ladění, AJI, výstup
        // Vše další (zadávání ladění, varhanní panel, MIDI osnovy) se kreslí pod ně.
        private const int HeaderRows = 4;

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
        private FormField _fMelodicRegister = null!;   // krok31: univerzální tónový rejstřík (1-128), 0 = skutečné nástroje ze souboru
        private FormField _fDrumRegister = null!;   // krok37: totéž, ale bicí sada (kit) na kanálu D10, 0 = skutečná sada ze souboru

        // krok16: rejstřík = banka + číslo + zap/vyp, stejná pole jako všechno ostatní (Tab, kolečko, +/-, číslice, Enter).
        // Pole jsou přidána AŽ ZA ostatní, aby pořadí Tab odpovídalo pořadí na obrazovce.
        private FormField _fRegBank = null!;
        private FormField _fRegNumber = null!;
        private FormField _fRegOn = null!;
        private int _selBank = 1;      // vybraná banka (zatím nemusí být zapnutá)
        private int _selNumber = 1;    // vybraný rejstřík v bance

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

            // krok31: univerzální tónový rejstřík uložený z minula (0-128,
            // viz pole UniR). Tady se netřeba hlídat proti nedostupné hodnotě
            // jako u bank výš - celý rozsah 0-128 je vždycky platný, jen 0
            // znamená "žádný" a 1-128 se případně samo nahradí výchozím
            // rejstříkem, když v souboru SF2 zrovna není (viz Sf2MidiSynth.Create).
            _midiPlayer.SetMidiMelodicRegister(_settings.MidiMelodicRegister);

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

            // L/R (zvukovka) jen když skutečně hraje soubor přes AudioPlayer
            // - u .mid souborů jde zvuk mimo AudioPlayer (Windows GS
            // Wavetable Synth), takže by tu nebylo co měřit.
            if (!_isMidiMode)
            {
                var (peakL, peakR) = _audioPlayer.ReadPeakLevels();
                float organPeakForLR = App.OrganEngine?.ReadPeak() ?? 0f;
                _leftDb = AudioMeter.LinearToDecibels(Math.Max(peakL, organPeakForLR));
                _rightDb = AudioMeter.LinearToDecibels(Math.Max(peakR, organPeakForLR));
            }

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
            // "lidské" GM číslování 1-128 (1 = Acoustic Grand Piano, staré
            // natvrdo zapsané chování). 0 = žádný univerzální rejstřík - hrají
            // skutečné nástroje podle Program Change v souboru (viz Sf2MidiSynth).
            // krok37: pole UniR teď navazuje hned na MidB (banka + její rejstřík
            // pohromadě), DrmB/DrmR mají stejnou dvojici hned za sebou níž.
            var pMelodicRegister = Parameter.Number(
                "UniR", "", 0, 128, 0, new[] { 1.0, 1.0, 10.0 },
                () => _settings.MidiMelodicRegister,
                v =>
                {
                    int program = (int)Math.Round(v);
                    _midiPlayer.SetMidiMelodicRegister(program);
                    _settings.MidiMelodicRegister = program;
                    ScheduleSettingsSave();
                },
                zeroPad: 3,
                defaultValue: 1.0);   // '*' vrátí na výchozí Acoustic Grand Piano
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

            // ---- krok16: banka, rejstřík, zap/vyp ----
            var banks = TE.Banks;
            _selBank = banks.Count > 0 ? banks[0] : 1;
            var nums = TE.NumbersInBank(_selBank);
            _selNumber = nums.Count > 0 ? nums[0] : 0;

            var pBank = Parameter.Number(
                "Banka", "", 0, 999, 0, new[] { 1.0, 1.0, 10.0 },
                () => _selBank,
                v => SelectBank((int)Math.Round(v)),
                zeroPad: 3);
            pBank.Stepper = (cur, dir) => StepInList(TE.Banks, cur, dir);   // +/- procházejí existující banky
            _fRegBank = _form.Add(pBank);

            var pNum = Parameter.Number(
                "Rej", "", 0, 99999, 0, new[] { 1.0, 1.0, 10.0 },   // krok17: až 99999 (čísla z SF2 bank, např. 8040)
                () => _selNumber,
                v => _selNumber = (int)Math.Round(v),
                zeroPad: 3);
            pNum.Stepper = (cur, dir) => StepInList(TE.NumbersInBank(_selBank), cur, dir);   // +/- další existující rejstřík
            pNum.OnTypedCommit = v => ToggleTypedRegister((int)Math.Round(v));               // "85 Enter" přepne rejstřík
            _fRegNumber = _form.Add(pNum);

            _fRegOn = _form.Add(Parameter.Toggle(
                "Zap",
                () => App.OrganEngine?.IsRegisterActive(new RegisterId(_selBank, _selNumber)) ?? false,
                on =>
                {
                    var id = new RegisterId(_selBank, _selNumber);
                    var engine = App.OrganEngine;
                    if (engine == null || !TE.RegisterExists(id)) return;
                    if (engine.IsRegisterActive(id) != on) engine.ToggleRegister(id);
                }));
        }

        /// <summary>krok16: změna banky; když vybraný rejstřík v nové bance není, skočí se na její první.</summary>
        private void SelectBank(int bank)
        {
            _selBank = bank;
            var nums = TE.NumbersInBank(bank);
            if (nums.Count > 0 && !nums.Contains(_selNumber)) _selNumber = nums[0];
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
                    if (TE.RegisterExists(new RegisterId(b, number))) { SelectBank(b); _selNumber = number; found = true; break; }
                }
                if (!found) return;
                id = new RegisterId(_selBank, number);
            }
            App.OrganEngine?.ToggleRegister(id);
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
            desiredRows = Math.Clamp(desiredRows, HeaderRows, 60);
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

            // ============ Řádek 2: VU metry L a P (u MIDI přehled) ============
            _grid.SetCursorPosition(0, 2);

            if (!_isMidiMode)
            {
                const int meterWidth = 44;

                _grid.Write(" L:[");
                _grid.ForegroundColor = ConsoleColor.Green;
                _grid.Write(AudioMeter.RenderBar(_leftDb, meterWidth));
                _grid.ResetColor();
                _grid.Write($"]{_leftDb,6:F1}dB  ");

                _grid.Write("R:[");
                _grid.ForegroundColor = ConsoleColor.Green;
                _grid.Write(AudioMeter.RenderBar(_rightDb, meterWidth));
                _grid.ResetColor();
                _grid.Write($"]{_rightDb,6:F1}dB");
            }
            else
            {
                int channelCount;
                lock (_midiNotesLock) { channelCount = _midiUsedChannels.Length; }

                _grid.ForegroundColor = ConsoleColor.DarkGray;
                _grid.Write($" MIDI ({_midiPlayer.OutputDescription}), kanálů: {channelCount}");   // krok20: skutečný zdroj zvuku
                _grid.ResetColor();
            }

            _grid.Write(new string(' ', Math.Max(0, Cols - _grid.CursorX)));
            _grid.WriteLine();

            // ============ Řádek 3: čas, textová pole (hlasitost, bicí, ladění, AJI), stav ============
            // Pole [ ] jsou ovladatelná myší (klik, kolečko), Tab, +/- a zadáním
            // čísla - viz FieldForm v Core. Každé pole se při vykreslení ohlásí
            // formuláři (Place), aby věděl, kam kliknout.
            TimeSpan current = _isMidiMode ? _midiPlayer.CurrentTime : _audioPlayer.CurrentTime;
            TimeSpan total = _isMidiMode ? _midiPlayer.TotalTime : _audioPlayer.TotalTime;
            string timeStr = $"{current:mm\\:ss}/{total:mm\\:ss}";

            _form.BeginFrame();
            _grid.SetCursorPosition(0, 3);
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

            // krok29: pole "Lad" zrušeno - T a A4 se zobrazují vždy, odchylka
            // v centech jen když není neutrální (T=0 a A4=440).
            // krok38: na přání zkráceno "T " -> "T" a "st  " -> "  " (ušetří
            // místo na řádku stavového řádku, kam se teď vejde i DrmR).
            WriteField(_fTranspose, "T", "  ");
            WriteField(_fA4, "A4 ", "Hz  ");

            if (!_tuning.IsNeutral)
            {
                Segment($"{_tuning.TotalCents:+0.0;-0.0;+0.0}c  ", ConsoleColor.Yellow);
            }

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

        private void RenderStatusArea()
        {
            bool showOrganPanel = App.Input?.IsLiveDeviceConnected ?? false;

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
            // (viz RenderDashboard, jsou to textová pole). Tady zbývá jen
            // varhanní panel a MIDI osnovy.
            // NOVÉ: formulář MIDI vstupů - jeden řádek na každé rozhraní (i odpojené, šedě).
            MidiPortSnapshot[] midiPorts = App.Input?.GetPorts() ?? Array.Empty<MidiPortSnapshot>();

            int totalRows = midiPorts.Length + (showOrganPanel ? 2 : 0) + staffRows;   // ZMĚNA: jen Reg a Akt, tři VU pásma (H/S/V) zrušena
            if (EnsureRows(HeaderRows + totalRows))
            {
                RenderDashboard();
            }

            _grid.SetCursorPosition(0, HeaderRows);

            RenderMidiPortRows(midiPorts);

            if (showOrganPanel)
            {
                // krok16: řádek Reg: = pole Banka (jen když je víc bank), Rejstřík, zap/vyp a dlouhé jméno
                bool multiBank = TE.Banks.Count > 1;
                int regRow = _grid.CursorY;
                _grid.ForegroundColor = ConsoleColor.Cyan;
                _grid.Write(" Reg: ");
                if (multiBank) WriteFormField(regRow, _fRegBank, "Banka ", "  ");
                WriteFormField(regRow, _fRegNumber, "Rej ", "  ");
                WriteFormField(regRow, _fRegOn, "", "  ");

                var selId = new RegisterId(_selBank, _selNumber);
                string? regName = TE.RegisterName(selId);
                string nameText;
                if (regName == null)
                    nameText = BankTable.Find(_selBank) == null ? "(banka neexistuje)" : "(rejstřík v bance neexistuje)";
                else
                    nameText = $"{_selNumber:000} {regName}";
                if (multiBank)
                    nameText = $"{BankTable.Find(_selBank)?.FullName ?? _selBank.ToString("000")} · {nameText}";
                _grid.ForegroundColor = regName == null ? ConsoleColor.DarkGray : ConsoleColor.White;

                // krok17: běží-li načítání banky SF2, ukáže se místo jména průběh; případná chyba načítání červeně
                string? loadStatus = PS150.Core.Sampler.Sf2Loader.StatusText;
                string? loadError = PS150.Core.Sampler.Sf2Loader.LastError;
                if (loadStatus != null)
                {
                    nameText = loadStatus;
                    _grid.ForegroundColor = ConsoleColor.Yellow;
                }
                else if (loadError != null && regName == null)
                {
                    nameText = loadError;
                    _grid.ForegroundColor = ConsoleColor.Red;
                }

                _grid.WriteLine(TextLayout.FitWidth(nameText, Math.Max(0, Cols - _grid.CursorX)));
                _grid.ResetColor();

                _grid.Write(" Akt: ");
                var active = App.OrganEngine?.ActiveRegisters;
                if (active != null && active.Count > 0)
                {
                    var ordered = active.OrderBy(r => r.Bank).ThenBy(r => r.Number).ToList();
                    string Label(RegisterId r, bool withName)
                    {
                        string code = multiBank ? r.ToString() : r.Number.ToString("000");
                        string? n = withName ? TE.RegisterName(r) : null;
                        return n == null ? code : $"{code} {n}";
                    }
                    string text = string.Join(", ", ordered.Select(r => Label(r, true)));
                    if (text.Length > Cols - 6)
                        text = string.Join(", ", ordered.Select(r => Label(r, false)));   // dlouhá jména se nevejdou
                    _grid.ForegroundColor = ConsoleColor.Green;
                    _grid.WriteLine(TextLayout.FitWidth(text, Cols - 6));
                    _grid.ResetColor();
                }
                else
                {
                    _grid.WriteLine(TextLayout.FitWidth("(žádný)", Cols - 6));
                }
            }

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
        }

    }
}
