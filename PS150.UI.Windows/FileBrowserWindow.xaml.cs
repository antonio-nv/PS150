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
        private FormField _fTuneOn = null!;
        private FormField _fTranspose = null!;
        private FormField _fA4 = null!;
        private FormField _fAji = null!;

        // Odložené ukládání nastavení: kolečko dělá desítky změn za sekundu,
        // settings.json se zapíše až po chvíli klidu (a při zavření okna).
        private DispatcherTimer _settingsSaveTimer = null!;

        private readonly StringBuilder _registerInputBuffer = new StringBuilder();

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
            _tuning = new PlayerTuning(
                _settings.TuningActive,
                _settings.TuningTransposeSemitones,
                _settings.TuningReferenceA4Hz);
            _midiPlayer.SetTuning(_tuning);
            _audioPlayer.SetSpeedFactor(_tuning.SpeedFactor);

            // NOVÉ: AJI uložené z minula (klávesa a).
            _midiPlayer.SetAjiEnabled(_settings.AjiEnabled);

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
            if (!_isMidiMode)
            {
                if (key >= Key.D0 && key <= Key.D9)
                {
                    if (_registerInputBuffer.Length < 3)
                        _registerInputBuffer.Append((char)('0' + (key - Key.D0)));
                    return true;
                }

                if (key >= Key.NumPad0 && key <= Key.NumPad9)
                {
                    if (_registerInputBuffer.Length < 3)
                        _registerInputBuffer.Append((char)('0' + (key - Key.NumPad0)));
                    return true;
                }

                if (key == Key.Back)
                {
                    if (_registerInputBuffer.Length > 0) _registerInputBuffer.Length--;
                    return true;
                }

                if (key == Key.Enter)
                {
                    if (_registerInputBuffer.Length > 0)
                    {
                        if (int.TryParse(_registerInputBuffer.ToString(), out int registerNumber))
                            App.OrganEngine?.ToggleRegister(registerNumber);
                        _registerInputBuffer.Clear();
                    }
                    return true;
                }
            }

            switch (key)
            {
                case Key.Escape:
                    if (_registerInputBuffer.Length > 0)
                    {
                        _registerInputBuffer.Clear();
                        return true;
                    }
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
                    // NOVÉ: ladění zapnout/vypnout (hodnoty se zachovají)
                    ApplyTuning(_tuning.WithActive(!_tuning.Active));
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

            _settings.TuningActive = tuning.Active;
            _settings.TuningTransposeSemitones = tuning.TransposeSemitones;
            _settings.TuningReferenceA4Hz = tuning.ReferenceA4Hz;
            ScheduleSettingsSave();
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

            _fTuneOn = _form.Add(Parameter.Toggle(
                "Lad",
                () => _tuning.Active,
                on => ApplyTuning(_tuning.WithActive(on))));

            // Změna hodnoty ladění ho zároveň zapne (jako dřív zadání přes INSERT).
            _fTranspose = _form.Add(Parameter.Number(
                "T", "st",
                -PlayerTuning.MaxTransposeSemitones, PlayerTuning.MaxTransposeSemitones, 2,
                new[] { 0.01, 0.5, 1.0 },
                () => _tuning.TransposeSemitones,
                v => ApplyTuning(new PlayerTuning(true, v, _tuning.ReferenceA4Hz)),
                showSign: true,
                defaultValue: 0.0));   // ZMĚNA: '*' vrátí transpozici na nulu

            _fA4 = _form.Add(Parameter.Number(
                "A4", "Hz",
                PlayerTuning.MinReferenceA4Hz, PlayerTuning.MaxReferenceA4Hz, 1,
                new[] { 0.1, 1.0, 5.0 },
                () => _tuning.ReferenceA4Hz,
                v => ApplyTuning(new PlayerTuning(true, _tuning.TransposeSemitones, v)),
                defaultValue: 440.0));   // ZMĚNA: '*' vrátí A4 na 440 Hz

            _fAji = _form.Add(Parameter.Toggle(
                "AJI",
                () => _midiPlayer.AjiEnabled,
                on =>
                {
                    _midiPlayer.SetAjiEnabled(on);
                    _settings.AjiEnabled = on;
                    ScheduleSettingsSave();
                }));
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
                _grid.Write($" MIDI (Microsoft GS Wavetable Synth), kanálů: {channelCount}");
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

            WriteField(_fTuneOn, "Lad ", "  ");
            WriteField(_fTranspose, "T ", "st  ");
            WriteField(_fA4, "A4 ", "Hz  ");

            if (_tuning.Active)
            {
                Segment($"{_tuning.TotalCents:+0.0;-0.0;+0.0}c  ", ConsoleColor.Yellow);
            }

            WriteField(_fAji, "AJI ", "  ");

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
                _grid.Write(" Reg:[");
                _grid.ForegroundColor = ConsoleColor.Red;
                _grid.Write(_registerInputBuffer.ToString().PadRight(3));
                _grid.ResetColor();
                _grid.WriteLine(TextLayout.FitWidth("] Ent/Esc/Bksp", Cols - 6 - 3));

                _grid.Write(" Akt: ");
                var active = App.OrganEngine?.ActiveRegisters;
                if (active != null && active.Count > 0)
                {
                    _grid.ForegroundColor = ConsoleColor.Green;
                    _grid.WriteLine(TextLayout.FitWidth(string.Join(",", active), Cols - 6));
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
