using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PS150.Core;

namespace PS150.UI.Windows
{
    internal static class VgaEngine
    {
        // Pevná šířka celého VGA okna ve znacích - VŠECHNO ostatní (oddělovací
        // čáry, VU metry, wrapping cesty k souboru...) se od tohohle čísla
        // odvíjí, ať se to na jednom místě dá později přeladit.
        private const int ConsoleWidth = 30;
        private const int ConsoleHeight = 45;

        // DŮLEŽITÉ: nikdy nepoužívat ConsoleWidth přímo pro obsah řádku, který
        // pak jde do Console.WriteLine()! Když text zaplní úplně celou šířku
        // okna a hned za tím přijde odřádkování, Windows konzole si sama vloží
        // fantomový prázdný řádek navíc (známá zvláštnost "delayed line wrap"),
        // což při pevné výšce bufferu vynutí scroll o řádek a rozjede všechny
        // absolutní SetCursorPosition souřadnice používané jinde v kódu -
        // přesně tohle způsobovalo promíchané/utíkající řádky. Proto se u
        // veškerého obsahu, po kterém následuje nový řádek, používá o 1 znak
        // užší ContentWidth - poslední sloupec zůstává vždy prázdný.
        private const int ContentWidth = ConsoleWidth - 1;

        // Kolikátým řádkem začíná obsah pod cestou k souboru (metry / MIDI
        // osnovy) - proměnlivé, protože cesta k souboru se teď zalamuje na
        // víc řádků podle délky (viz WrapPath), na rozdíl od dřívějška, kdy
        // to bylo natvrdo na řádku 7.
        private static int _contentStartRow = 7;

        // Vlastní (spolehlivější) tažení okna myší - viz StartWindowDrag /
        // UpdateWindowDrag níž. Nespoléhá se na WM_NCLBUTTONDOWN modální
        // smyčku, protože ta čeká na SKUTEČNÉ puštění tlačítka myši a kvůli
        // zpoždění fronty konzole se snadno stane, že tlačítko je puštěné
        // už dřív, než se k tomu Windows vůbec dostane - a smyčka se pak
        // zasekne navěky (řešilo se to i klávesou Alt+F4).
        private static bool _isDragging = false;
        private static int _dragOffsetX = 0;
        private static int _dragOffsetY = 0;

        // Poslední okamžik, kdy se RenderDashboard() zavolal "jen tak", kvůli
        // plynoucímu aktuálnímu času - viz smyčka v Run().
        private static DateTime _lastTimeRefresh = DateTime.MinValue;

        private static DirectoryNavigator _navigator = new();
        private static AudioPlayer _audioPlayer = new();

        // Perzistentní nastavení (settings.json) - načteno jednou při startu
        // konzole. Volume se do něj ukládá při každé změně hlasitosti (viz
        // ChangeVolume), ať se skutečná hodnota nezapomíná, jak se dělo dřív.
        private static AppSettings _settings = new();

        // Přehrávač .mid souborů na PC (natvrdo klavír přes Microsoft GS Wavetable
        // Synth) - viz GmPianoMidiPlayer.cs. Úmyslně NEpoužívá PS150.Core.
        private static GmPianoMidiPlayer _midiPlayer = new();

        // Sdílený stav právě znějících not (kanál, MIDI číslo noty) pro jednoduchý
        // stackovaný náhled osnov níž (RenderMidiStaffOnly). Aktualizuje se
        // z přehrávacího vlákna GmPianoMidiPlayer, čte se z hlavního vlákna
        // konzole - proto zámek.
        private static readonly object _midiNotesLock = new object();
        private static readonly HashSet<(int Channel, int Note)> _midiActiveNotes = new();

        // Seznam kanálů (notových osnov), které aktuálně přehrávaný .mid soubor
        // vůbec používá - zjištěno jednorázově při načtení souboru (viz
        // GmPianoMidiPlayer.ChannelsDetected). Díky tomu se ve VGA konzoli
        // zobrazují všechny osnovy trvale (i tiché), místo aby chaoticky
        // vyskakovaly a mizely jen podle toho, co zrovna zní.
        private static int[] _midiUsedChannels = Array.Empty<int>();
        private static bool _midiFinishedNaturally = false;
        private static string? _midiErrorMessage = null;
        private static bool _midiPlayerEventsWired = false;
        private static bool _settingsLoaded = false;

        private static double _leftDb = -120.0;
        private static double _rightDb = -120.0;

        // Tři pásma (hloubky/středy/výšky) jen ze syntezátoru PS150.Core -
        // NEjsou smíchané s výstupem přehrávače souborů, na rozdíl od _leftDb/_rightDb výše.
        private static double _bassDb = -120.0;
        private static double _midDb = -120.0;
        private static double _trebleDb = -120.0;
        private static int _volume = 80;
        private static bool _isPaused = false;

        private static bool _isMidiMode = false;

        // Buffer pro číselné zadávání čísla rejstříku (viz HandleInput / RenderMetersOnly)
        private static readonly StringBuilder _registerInputBuffer = new StringBuilder();

        #region Windows API pro konzoli a myš
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Nastaveno jednou v Run() hned po GetConsoleWindow() - ať má
        // RenderDashboard() přístup k handle okna a pozná, jestli je zrovna
        // aktivní, aniž by se muselo měnit jeho podpis (volá se odjinud na
        // desítkách míst v celém souboru).
        private static IntPtr _consoleHwnd = IntPtr.Zero;

        // --- Odstranění title baru + náhradní tažení myší za dekorativní
        // horní okraj konzole (řádek y==0), viz použití v Run()/mouse handleru níž.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;
        private const int WS_THICKFRAME = 0x00040000; // "úchyt" pro tažení za okraj - taky pryč, ať jde velikost fixní

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_PROCESSED_INPUT = 0x0001;
        private const uint ENABLE_LINE_INPUT = 0x0002;
        private const uint ENABLE_ECHO_INPUT = 0x0004;
        private const uint ENABLE_MOUSE_INPUT = 0x0010;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040; // "QuickEdit" - VÝCHOZÍ ZAPNUTÉ na většině Windows instalací
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        private const ushort MOUSE_EVENT = 0x0002;
        private const uint MOUSE_WHEELED = 0x0004;
        private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleInput, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleInput, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpNumberOfEvents);

        private const ushort KEY_EVENT = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
            [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEY_EVENT_RECORD
        {
            // Nativně je to Win32 BOOL (4 bajty) - schválně čteno jako int,
            // ne jako C# bool. "bool" v P/Invoke struktuře je notoricky
            // křehké místo (marshaling se snadno netrefí přesně na hranici
            // bajtů) a hodnota pak vychází vždycky false, takže by se
            // klávesové události tiše zahazovaly úplně všechny - přesně to,
            // co jsme viděli (myš fungovala, klávesnice ne).
            public int bKeyDown;
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char uChar;
            public uint dwControlKeyState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSE_EVENT_RECORD
        {
            public COORD dwMousePosition;
            public uint dwButtonState;
            public uint dwControlKeyState;
            public uint dwEventFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }
        #endregion

        // Nastaveno při navigaci (PageUp/PageDown i automatickém doehrání
        // skladby), pokud další soubor v pořadí vyjde jako VIDEO - VgaEngine
        // sama sebe ukončí (running=false) a tohle je cesta, kterou má
        // MediaLauncher předat MainWindow. Zůstává null, pokud uživatel
        // jen normálně skončil (Escape/[X]) bez navazujícího souboru.
        private static string? _handoffFile = null;

        /// <returns>
        /// Cestu k souboru, na který navigace "přejela" mimo doménu VGA
        /// (tzn. video), a kam by měl MediaLauncher pokračovat spuštěním
        /// MainWindow. Null, pokud uživatel jen normálně skončil.
        /// </returns>
        public static string? Run(string filePath)
        {
            _handoffFile = null;

            // Načtení uloženého nastavení (hlasitost atd.) - jen jednou, při
            // prvním spuštění konzole. Dřív se _volume vždycky natvrdo
            // inicializovalo na 80 bez ohledu na settings.json.
            if (!_settingsLoaded)
            {
                _settings = AppSettings.Load();
                _volume = _settings.Volume;
                _settingsLoaded = true;
            }

            // Napojení sledování aktivních not pro náhled osnov - jen jednou,
            // ať se při dalších voláních Run() (další soubor) neregistrují
            // duplicitní handlery.
            if (!_midiPlayerEventsWired)
            {
                _midiPlayer.NoteOnRaised += (channel, note) =>
                {
                    lock (_midiNotesLock) { _midiActiveNotes.Add((channel, note)); }
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
                _midiPlayerEventsWired = true;
            }

            // 1. Otevření konzole a vynucení fokusu
            AllocConsole();
            IntPtr hwnd = GetConsoleWindow();
            _consoleHwnd = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);

                // Sundat title bar (ikonka, titulek, Minimalizovat/Maximalizovat/Zavřít) -
                // od téhle chvíle okno nejde tažením za horní pruh přesouvat standardní
                // cestou. Náhrada je níž v mouse handleru (y==0 = náš vlastní textový
                // title bar). WS_THICKFRAME pryč taky - ať nejde okno tažením za okraj
                // zvětšit/zmenšit, velikost je teď pevně daná (viz ConsoleWidth/Height).
                int style = GetWindowLong(hwnd, GWL_STYLE);
                SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_CAPTION | WS_THICKFRAME));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);

                // Obnovení uložené polohy okna (viz konec smyčky níž, kde se
                // ukládá) - jen když souřadnice pořád dávají smysl vzhledem
                // k AKTUÁLNĚ připojeným monitorům. Bez týhle kontroly by se
                // po odpojení monitoru nebo změně sestavy okno mohlo otevřít
                // mimo viditelnou plochu a nešlo by na něj myší vůbec
                // dosáhnout.
                if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
                {
                    int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
                    int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
                    int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                    int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                    // Nestačí jen "levý horní roh je někde na virtuální ploše" -
                    // necháváme aspoň kousek title baru (50 px) viditelný, ať
                    // je za co okno příště chytit a přetáhnout, i kdyby zbytek
                    // vyčníval mimo.
                    const int minVisible = 50;
                    bool withinBounds =
                        _settings.WindowX.Value >= vLeft - minVisible &&
                        _settings.WindowX.Value <= vLeft + vWidth - minVisible &&
                        _settings.WindowY.Value >= vTop &&
                        _settings.WindowY.Value <= vTop + vHeight - minVisible;

                    if (withinBounds)
                    {
                        SetWindowPos(hwnd, IntPtr.Zero, _settings.WindowX.Value, _settings.WindowY.Value, 0, 0,
                            SWP_NOSIZE | SWP_NOZORDER);
                    }
                }
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;
            Console.Title = "PS150 - VGA Console Engine";

            try
            {
                // Nejdřív zmenšit okno na minimum - Windows konzole si jinak
                // stěžuje ("parameter is out of range"), pokud by se nová
                // šířka bufferu měla nastavit menší, než je aktuální velikost
                // okna (a naopak). Zmenšením na 1x1 napřed se týhle
                // kombinaci vždycky vyhneme, ať už uživatel měl konzoli
                // předtím jakkoliv velkou.
                Console.SetWindowSize(1, 1);
                Console.SetBufferSize(ConsoleWidth, ConsoleHeight);
                Console.SetWindowSize(ConsoleWidth, ConsoleHeight);
            }
            catch { }

            // 2. Nastavení režimu vstupu konzole
            IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
            if (GetConsoleMode(hInput, out uint mode))
            {
                mode |= ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
                // ENABLE_QUICK_EDIT_MODE (0x0040) je na většině Windows instalací
                // ve výchozím nastavení konzole ZAPNUTÉ - a dokud zůstane, Windows
                // si tažení myší a Ctrl+C bere pro sebe (výběr/kopírování textu)
                // dřív, než se k nám vůbec dostane jako MOUSE_EVENT. Musí se
                // VYPNOUT výslovně (nestačí ho jen "nezapínat") a zároveň musí
                // zůstat nastavené ENABLE_EXTENDED_FLAGS - jinak Windows tuhle
                // změnu potichu ignoruje.
                mode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_QUICK_EDIT_MODE);
                SetConsoleMode(hInput, mode);
            }

            if (File.Exists(filePath))
            {
                _navigator.LoadDirectory(filePath);
                UpdateFileTypeState();
            }

            RenderDashboard();

            bool running = true;

            while (running)
            {
                // A)+B) Čtení VŠECH čekajících událostí (klávesnice i myš)
                // JEDNÍM stejným Win32 mechanismem. Dřív se klávesnice četla
                // přes .NET Console.ReadKey() a myš zvlášť přes syrové Win32
                // volání na STEJNÉM vstupním handle - to jsou ale dvě
                // vzájemně nekompatibilní cesty čtení ze stejné fronty a
                // kombinace obou nespolehlivě "krade" události druhé straně
                // (tím se ztrácely myší události). Teď se čte výhradně
                // tudy, pro oba typy vstupu stejně - ConsoleKey hodnoty
                // jsou navržené tak, že přímo odpovídají Windows Virtual-Key
                // kódům, takže wVirtualKeyCode jde na ConsoleKey přetypovat
                // rovnou, beze ztráty.
                GetNumberOfConsoleInputEvents(hInput, out uint pending);
                if (pending > 0)
                {
                    var buffer = new INPUT_RECORD[pending];
                    ReadConsoleInput(hInput, buffer, pending, out uint eventsRead);

                    for (int i = 0; i < eventsRead && running; i++)
                    {
                        var record = buffer[i];

                        if (record.EventType == KEY_EVENT && record.KeyEvent.bKeyDown != 0)
                        {
                            running = HandleInput((ConsoleKey)record.KeyEvent.wVirtualKeyCode);
                            RenderDashboard();
                        }
                        else if (record.EventType == MOUSE_EVENT && record.MouseEvent.dwEventFlags == MOUSE_WHEELED)
                        {
                            int scrollDelta = (int)record.MouseEvent.dwButtonState >> 16;
                            ChangeVolume(scrollDelta > 0 ? 5 : -5);
                            RenderDashboard();
                        }
                        else if (record.EventType == MOUSE_EVENT && record.MouseEvent.dwEventFlags == 0)
                        {
                            if ((record.MouseEvent.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                            {
                                short mx = record.MouseEvent.dwMousePosition.X;
                                short my = record.MouseEvent.dwMousePosition.Y;

                                if (my == 0)
                                {
                                    // Textový title bar - buď kliknutí na jeden z
                                    // [_][□][X] ovladačů, nebo tažení za zbytek řádku.
                                    running = HandleTitleBarClick(mx, hwnd);
                                }
                                else if (my == TransportButtonsRow && IsOnTransportButton(mx))
                                {
                                    HandleMouseClick(mx, my);
                                }
                                else if (hwnd != IntPtr.Zero)
                                {
                                    // Kdekoliv jinde na formuláři (cesta, metry, prázdné
                                    // plochy...) -> tažení okna myší, viz StartWindowDrag.
                                    StartWindowDrag(hwnd);
                                }
                                RenderDashboard();
                            }
                        }
                    }
                }

                if (!running) break;

                // Tažení okna (viz StartWindowDrag/UpdateWindowDrag) se
                // aktualizuje KAŽDÝ tik, ne jen když přijde nová událost z
                // konzole - tak sleduje aktuální polohu myši plynule.
                UpdateWindowDrag(hwnd);

                // RenderDashboard() se jinak volá jen při klávese/kliknutí -
                // takže se aktuální čas (na rozdíl od celkového, který se
                // spočítá jednou při načtení souboru) sám od sebe nikdy
                // neposouval. Jednou za sekundu ho i bez žádné akce uživatele
                // překreslíme, ať čas viditelně běží dál.
                if ((DateTime.UtcNow - _lastTimeRefresh).TotalSeconds >= 1)
                {
                    RenderDashboard();
                    _lastTimeRefresh = DateTime.UtcNow;
                }
                if (!_isMidiMode && !_isPaused && _audioPlayer.TotalTime > TimeSpan.Zero)
                {
                    if (_audioPlayer.CurrentTime >= _audioPlayer.TotalTime - TimeSpan.FromMilliseconds(300))
                    {
                        string? nextFile = _navigator.GetNextFile();
                        if (nextFile != null && MediaKind.IsVideo(nextFile))
                        {
                            _handoffFile = nextFile;
                            running = false;
                            break;
                        }
                        UpdateFileTypeState();
                        Console.Clear();
                        RenderDashboard();
                    }
                }
                else if (_isMidiMode)
                {
                    // Stejné chování jako u zvukových souborů výše - jen zdroj
                    // informace "dohráno" je jiný (event z GmPianoMidiPlayer
                    // místo porovnávání CurrentTime/TotalTime).
                    bool finishedNaturally;
                    lock (_midiNotesLock)
                    {
                        finishedNaturally = _midiFinishedNaturally;
                        _midiFinishedNaturally = false;
                    }

                    if (finishedNaturally)
                    {
                        string? nextFile = _navigator.GetNextFile();
                        if (nextFile != null && MediaKind.IsVideo(nextFile))
                        {
                            _handoffFile = nextFile;
                            running = false;
                            break;
                        }
                        UpdateFileTypeState();
                        Console.Clear();
                        RenderDashboard();
                    }
                }

                // D) Vykreslení VU metrů / MIDI osnovy
                if (!_isMidiMode)
                {
                    var (peakL, peakR) = _audioPlayer.ReadPeakLevels();

                    // Úroveň živě hraných varhan (mono) - promítne se do obou kanálů,
                    // pokud je v tu chvíli hlasitější než přehrávaný soubor. Když je
                    // přehrávání pozastavené (peakL/peakR = 0), ukáže VU metr čistě
                    // hru na varhany.
                    float organPeak = App.OrganEngine?.ReadPeak() ?? 0f;
                    float combinedL = Math.Max(peakL, organPeak);
                    float combinedR = Math.Max(peakR, organPeak);

                    _leftDb = AudioMeter.LinearToDecibels(combinedL);
                    _rightDb = AudioMeter.LinearToDecibels(combinedR);

                    // Tři pásma (hloubky/středy/výšky) čistě ze syntezátoru - úmyslně
                    // NEmíchat s peakL/peakR výše, ať metry ukazují jen to, co dělá
                    // ToneEngine/AudioEngine sám o sobě.
                    float bassPeak = 0f, midPeak = 0f, treblePeak = 0f;
                    if (App.OrganEngine != null)
                    {
                        var bandPeaks = App.OrganEngine.ReadBandPeaks();
                        bassPeak = bandPeaks.Bass;
                        midPeak = bandPeaks.Mid;
                        treblePeak = bandPeaks.Treble;
                    }

                    _bassDb = AudioMeter.LinearToDecibels(bassPeak);
                    _midDb = AudioMeter.LinearToDecibels(midPeak);
                    _trebleDb = AudioMeter.LinearToDecibels(treblePeak);

                    RenderMetersOnly();
                }
                else
                {
                    RenderMidiStaffOnly();
                }

                Thread.Sleep(30);
            }

            // Uložit polohu okna a naposledy přehrávaný soubor pro příští
            // spuštění (viz obnovení výše). DŘÍV se do settings.json ukládal
            // jen soubor, se kterým appka nastartovala - LastFilePath se pak
            // nikdy neaktualizoval podle skutečné navigace (PageUp/PageDown),
            // takže po zavření zůstal uložený ten úplně první soubor, ne ten
            // poslední přehrávaný.
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT finalRect))
            {
                _settings.WindowX = finalRect.Left;
                _settings.WindowY = finalRect.Top;
            }
            string? lastFile = _handoffFile ?? _navigator.CurrentFile;
            if (!string.IsNullOrEmpty(lastFile))
            {
                _settings.LastFilePath = lastFile;
                _settings.LastFolderPath = Path.GetDirectoryName(lastFile);
            }
            _settings.Save();

            _audioPlayer.Stop();
            _midiPlayer.Stop();
            Console.CursorVisible = true;

            // DŮLEŽITÉ: Run() se teď může za běh procesu zavolat vícekrát
            // (viz smyčka v MediaLauncher.Launch, co střídá video a
            // zvuk/MIDI) - bez tohohle by druhé volání AllocConsole() na
            // začátku Run() tiše selhalo (proces už jednou konzoli má) a
            // pracovalo by se dál se starou, možná už neplatnou konzolí.
            FreeConsole();

            return _handoffFile;
        }

        // Řádek s transportními tlačítky - musí sedět s RenderDashboard.
        private const int TransportButtonsRow = 3;

        private static bool IsOnTransportButton(short x) =>
            (x >= 1 && x <= 4) || (x >= 6 && x <= 8) || (x >= 10 && x <= 12) || (x >= 14 && x <= 17);

        // Title bar (řádek y==0) - vpravo tři ovladače [_][□][X], zbytek řádku
        // slouží jako úchyt pro tažení okna. Přesné x-souřadnice odpovídají
        // rozvržení v RenderDashboard (levý text + [_][□][X] zarovnané doprava).
        private static bool HandleTitleBarClick(short x, IntPtr hwnd)
        {
            const string ctrlBlock = "[_][□][X]";
            int ctrlStart = ContentWidth - ctrlBlock.Length; // musí sedět s paddingem v RenderDashboard

            if (x >= ctrlStart && x <= ctrlStart + 2)          // [_]
            {
                if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_MINIMIZE);
            }
            else if (x >= ctrlStart + 3 && x <= ctrlStart + 5) // [□] - záměrně vyřazeno, viz komentář u vykreslení
            {
                // Maximalizace u pevně velkého okna nedává smysl - tlačítko se
                // zobrazuje jen jako vzhledová připomínka klasického title baru
                // (vyšedivělé, viz RenderDashboard), klik na něj úmyslně nic nedělá.
            }
            else if (x >= ctrlStart + 6 && x <= ctrlStart + 8) // [X]
            {
                return false; // ukončí hlavní smyčku v Run() stejně jako Escape
            }
            else if (hwnd != IntPtr.Zero)
            {
                // Kdekoliv jinde na title baru -> tažení okna myší, viz StartWindowDrag.
                StartWindowDrag(hwnd);
            }
            return true;
        }

        /// <summary>
        /// Zapamatuje si posun mezi kurzorem a levým horním rohem okna a
        /// zapne sledování tažení - viz UpdateWindowDrag, volané z hlavní
        /// smyčky v Run() úplně nezávisle na frontě konzolových událostí.
        /// </summary>
        private static void StartWindowDrag(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!GetCursorPos(out POINT cursor)) return;
            if (!GetWindowRect(hwnd, out RECT rect)) return;

            _dragOffsetX = cursor.X - rect.Left;
            _dragOffsetY = cursor.Y - rect.Top;
            _isDragging = true;
        }

        /// <summary>
        /// Volá se z hlavní smyčky KAŽDÝ tik (ne jen když přijde událost
        /// myši z konzole - proto to nemá jejich zpoždění, viz komentář u
        /// _isDragging). Ptá se přímo na AKTUÁLNÍ stav levého tlačítka
        /// (GetAsyncKeyState), ne na to, co je zrovna ve frontě - takže se
        /// nemůže stát, že by čekala na "puštění", které se ve skutečnosti
        /// už dávno stalo (přesně to zamykalo starý SendMessage/WM_NCLBUTTONDOWN
        /// přístup).
        /// </summary>
        private static void UpdateWindowDrag(IntPtr hwnd)
        {
            if (!_isDragging) return;

            bool leftButtonDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (!leftButtonDown)
            {
                _isDragging = false;
                // Opakované SetWindowPos během tažení (okno patří jinému
                // procesu - conhost/Windows Terminal, ne nám) občas okno
                // připraví o klávesový fokus, i když vypadá pořád aktivní.
                // Po skončení tažení si ho radši vyžádáme zpátky výslovně.
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
                return;
            }

            if (hwnd != IntPtr.Zero && GetCursorPos(out POINT cursor))
            {
                SetWindowPos(hwnd, IntPtr.Zero, cursor.X - _dragOffsetX, cursor.Y - _dragOffsetY, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER);
            }
        }

        private static void HandleMouseClick(short x, short y)
        {
            // Řádek s transportními tlačítky - viz přesné rozvržení v RenderDashboard:
            // " [<<] [►] [▄] [>>] ♪♫"
            if (y == TransportButtonsRow)
            {
                if (x >= 1 && x <= 4)        // [<<]
                {
                    _audioPlayer.Seek(-5.0);
                }
                else if (x >= 6 && x <= 8)   // [►]
                {
                    if (_isPaused) TogglePlayPause();
                }
                else if (x >= 10 && x <= 12) // [▄]
                {
                    if (!_isPaused) TogglePlayPause();
                }
                else if (x >= 14 && x <= 17) // [>>]
                {
                    _audioPlayer.Seek(5.0);
                }
            }
        }

        private static void TogglePlayPause()
        {
            _isPaused = !_isPaused;

            // DŮLEŽITÉ: podle toho, jaký typ souboru zrovna hraje, se musí
            // pozastavit/spustit ten SPRÁVNÝ přehrávač - dřív se tu vždycky
            // sahalo jen na _audioPlayer, i když zrovna hrálo MIDI (ten pak
            // pauza vůbec neovlivnila, MID hrál dál bez ohledu na mezerník).
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

        private static void ChangeVolume(int delta)
        {
            _volume = Math.Clamp(_volume + delta, 0, 100);
            _audioPlayer.Volume = _volume / 100.0f;
            _midiPlayer.SetVolume(_volume);

            // Uložíme SKUTEČNOU aktuální hodnotu - dřív se sem nikdy
            // nezapsalo nic jiného než výchozích 80, protože se
            // AppSettings.Volume nikde neaktualizovalo před Save().
            _settings.Volume = _volume;
            _settings.Save();
        }

        private static void UpdateFileTypeState()
        {
            string currentFile = _navigator.CurrentFile ?? "";
            string ext = Path.GetExtension(currentFile).ToLowerInvariant();

            _isMidiMode = (ext == ".mid" || ext == ".midi" || ext == ".kar");

            if (_isMidiMode)
            {
                // DŮLEŽITÉ: zastavit případně ještě běžící zvukový soubor
                // (MP3/WAV/FLAC) - dřív se tu volalo jen _midiPlayer.PlayAsync()
                // a _audioPlayer se nechal běžet dál na pozadí, takže hrálo
                // obojí najednou (a mezerník pak omylem ovládal ten zvukový
                // soubor, ne MIDI - viz TogglePlayPause).
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
                    // Fire-and-forget - přehrávání běží na vlastním vlákně uvnitř
                    // GmPianoMidiPlayer, konzole se dál věnuje vykreslování a vstupu.
                    _ = _midiPlayer.PlayAsync(currentFile);
                    _midiPlayer.SetVolume(_volume);
                }
            }
            else
            {
                // Přechod z MIDI souboru na audio/jiný soubor - ukončíme případné
                // běžící MIDI přehrávání, ať nehraje na pozadí přes další skladbu.
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

        private static bool HandleInput(ConsoleKey key)
        {
            // --- Číselné zadávání čísla rejstříku (jen mimo MIDI režim) ---
            if (!_isMidiMode)
            {
                if (key >= ConsoleKey.D0 && key <= ConsoleKey.D9)
                {
                    if (_registerInputBuffer.Length < 3)
                    {
                        _registerInputBuffer.Append((char)('0' + (key - ConsoleKey.D0)));
                    }
                    return true;
                }

                if (key >= ConsoleKey.NumPad0 && key <= ConsoleKey.NumPad9)
                {
                    if (_registerInputBuffer.Length < 3)
                    {
                        _registerInputBuffer.Append((char)('0' + (key - ConsoleKey.NumPad0)));
                    }
                    return true;
                }

                if (key == ConsoleKey.Backspace)
                {
                    if (_registerInputBuffer.Length > 0)
                    {
                        _registerInputBuffer.Length--;
                    }
                    return true;
                }

                if (key == ConsoleKey.Enter)
                {
                    if (_registerInputBuffer.Length > 0)
                    {
                        if (int.TryParse(_registerInputBuffer.ToString(), out int registerNumber))
                        {
                            App.OrganEngine?.ToggleRegister(registerNumber);
                        }
                        _registerInputBuffer.Clear();
                    }
                    return true;
                }
            }

            switch (key)
            {
                case ConsoleKey.Escape:
                    // Pokud se právě píše číslo, Escape ho jen zruší - neukončuje
                    // celou konzoli. Teprve Escape na prázdném vstupu aplikaci ukončí.
                    if (_registerInputBuffer.Length > 0)
                    {
                        _registerInputBuffer.Clear();
                        return true;
                    }
                    return false;

                case ConsoleKey.Spacebar:
                // X, C, V - staré zvyky z Winampu, stejná funkce jako mezerník.
                case ConsoleKey.X:
                case ConsoleKey.C:
                case ConsoleKey.V:
                    TogglePlayPause();
                    break;

                // PgDown, nebo B (staré zvyky z Winampu) -> Další soubor
                case ConsoleKey.PageDown:
                case ConsoleKey.B:
                    {
                        string? nextFile = _navigator.GetNextFile();
                        if (nextFile != null)
                        {
                            if (MediaKind.IsVideo(nextFile))
                            {
                                _handoffFile = nextFile;
                                return false;
                            }
                            UpdateFileTypeState();
                            Console.Clear();
                        }
                    }
                    break;

                // PgUp, nebo Z (staré zvyky z Winampu) -> Předchozí soubor
                case ConsoleKey.PageUp:
                case ConsoleKey.Z:
                    {
                        string? prevFile = _navigator.GetPreviousFile();
                        if (prevFile != null)
                        {
                            if (MediaKind.IsVideo(prevFile))
                            {
                                _handoffFile = prevFile;
                                return false;
                            }
                            UpdateFileTypeState();
                            Console.Clear();
                        }
                    }
                    break;

                // Šipka doprava -> +5s
                case ConsoleKey.RightArrow:
                    if (_isMidiMode) _midiPlayer.Seek(5.0);
                    else _audioPlayer.Seek(5.0);
                    break;

                // Šipka doleva -> -5s
                case ConsoleKey.LeftArrow:
                    if (_isMidiMode) _midiPlayer.Seek(-5.0);
                    else _audioPlayer.Seek(-5.0);
                    break;

                // Hlasitost
                case ConsoleKey.UpArrow:
                    ChangeVolume(5);
                    break;

                case ConsoleKey.DownArrow:
                    ChangeVolume(-5);
                    break;
            }
            return true;
        }

        /// <summary>
        /// Rozlomí (typicky dlouhou) cestu k souboru na řádky nepřesahující
        /// ConsoleWidth. Kde to jde, láme se hned za zpětným lomítkem (ať
        /// nevznikají poloviny názvů adresářů uprostřed řádku) - jen když by
        /// takhle vzniklo příliš krátké torzo, láme se natvrdo po znacích.
        /// </summary>
        private static List<string> WrapPath(string text, int width)
        {
            var lines = new List<string>();
            int pos = 0;
            while (pos < text.Length)
            {
                int remaining = text.Length - pos;
                if (remaining <= width)
                {
                    lines.Add(text.Substring(pos));
                    break;
                }

                int breakAt = text.LastIndexOf('\\', pos + width - 1, width);
                if (breakAt <= pos) // žádné vhodné lomítko poblíž -> tvrdý zlom
                {
                    breakAt = pos + width - 1;
                }
                else
                {
                    breakAt++; // lomítko zůstane na konci předchozího řádku
                }

                lines.Add(text.Substring(pos, breakAt - pos));
                pos = breakAt;
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        private static void RenderDashboard()
        {
            Console.SetCursorPosition(0, 0);

            string currentFile = _navigator.CurrentFile ?? "No file loaded";
            string fileName = Path.GetFileName(currentFile);
            string folderPath = Path.GetDirectoryName(currentFile) ?? "";
            string fullPath = $"{folderPath}\\{fileName}";

            TimeSpan current = _isMidiMode ? _midiPlayer.CurrentTime : _audioPlayer.CurrentTime;
            TimeSpan total = _isMidiMode ? _midiPlayer.TotalTime : _audioPlayer.TotalTime;
            string timeStr = $"{current:mm\\:ss}/{total:mm\\:ss}";

            string separator = new string('=', ContentWidth);
            string thinSeparator = new string('-', ContentWidth);

            // --- Řádek 0: textový title bar ---
            // Vlevo "♪♫ PS150 Player.", vpravo [_][□][X] zarovnané na pravý
            // okraj, mezi tím pozadí táhnoucí se přes celý řádek. Barva se
            // liší podle toho, jestli je okno zrovna aktivní (má fokus) -
            // stejně jako u běžného Windows title baru.
            bool isActive = _consoleHwnd != IntPtr.Zero && GetForegroundWindow() == _consoleHwnd;

            const string titleText = "♪♫ PS150 Player.";
            const string ctrlBlock = "[_][□][X]";
            int padLen = Math.Max(0, ContentWidth - titleText.Length - ctrlBlock.Length);

            Console.BackgroundColor = isActive ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            Console.ForegroundColor = isActive ? ConsoleColor.White : ConsoleColor.DarkGray;
            Console.Write(titleText);
            Console.Write(new string(' ', padLen));
            Console.ForegroundColor = isActive ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
            Console.Write("[_]");
            Console.ForegroundColor = ConsoleColor.DarkGray; // maximalizace je záměrně vyřazená, viz HandleTitleBarClick
            Console.Write("[□]");
            Console.ForegroundColor = isActive ? ConsoleColor.White : ConsoleColor.DarkGray;
            Console.Write("[");
            Console.ForegroundColor = isActive ? ConsoleColor.Red : ConsoleColor.DarkGray;
            Console.Write("X");
            Console.ForegroundColor = isActive ? ConsoleColor.White : ConsoleColor.DarkGray;
            Console.Write("]");
            Console.ResetColor();
            Console.WriteLine();

            // --- Řádek 1: oddělovač ---
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(separator);

            // --- Řádek 2: hlasitost + čas ---
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($" Vol:{_volume,3}% [{timeStr}]".PadRight(ContentWidth));

            // --- Řádek 3: transportní tlačítka (souřadnice viz HandleMouseClick) ---
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(" [<<] ");

            bool isActuallyPlaying = _isMidiMode
                ? !_midiPlayer.IsPaused
                : _audioPlayer.IsPlaying;

            if (!_isPaused && isActuallyPlaying)
            {
                Console.BackgroundColor = ConsoleColor.Green;
                Console.ForegroundColor = ConsoleColor.Black;
                Console.Write("[►]");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[►]");
            }
            Console.Write(" ");

            if (_isPaused || !isActuallyPlaying)
            {
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("[▄]");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[▄]");
            }
            Console.Write(" ");

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("[>>] ");
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("♪♫");
            Console.ResetColor();
            Console.WriteLine();

            // --- Řádek 4: oddělovač ---
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(separator);
            Console.ResetColor();

            // --- Cesta k souboru, zalomená na šířku okna ---
            var pathLines = WrapPath(fullPath, ContentWidth - 1);
            foreach (string line in pathLines)
            {
                Console.WriteLine($" {line}".PadRight(ContentWidth));
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(thinSeparator);
            Console.ResetColor();

            // Řádky: title(1) + sep(1) + vol(1) + transport(1) + sep(1)
            //       + cesta (pathLines.Count) + tenký oddělovač(1)
            _contentStartRow = 5 + pathLines.Count + 1;
        }

        /// <summary>
        /// Zarovná text na přesně danou šířku - kratší text doplní mezerami,
        /// delší ořízne a označí "…". Používá se všude v úzkém 30znakovém
        /// layoutu, ať se žádný řádek nikdy neroztáhne mimo okno.
        /// </summary>
        // Poslední výška okna, o kterou jsme si SAMI požádali - EnsureWindowHeight()
        // provede skutečnou změnu jen když se aktuálně požadovaná výška liší
        // od tyhle, NE podle toho, co zrovna hlásí Console.WindowHeight. Ten
        // se totiž po SetWindowSize() nemusí ihned ustálit (hlavně ve Windows
        // Terminalu) - kdyby se porovnávalo přímo s ním při KAŽDÉM tiku (30x
        // za sekundu), mohlo by se to donekonečna přepočítávat a čistit, což
        // právě způsobovalo to "utíkání" okna pryč.
        private static int _lastDesiredHeight = -1;

        private static string FitWidth(string text, int width)
        {
            if (width <= 0) return "";
            if (text.Length <= width) return text.PadRight(width);
            return width == 1 ? text.Substring(0, 1) : text.Substring(0, width - 1) + "…";
        }

        /// <summary>
        /// Přizpůsobí výšku okna přesně tomu, kolik řádků obsah skutečně
        /// potřebuje. Skutečnou změnu provede jen tehdy, když se požadovaná
        /// výška doopravdy liší od poslední, o kterou jsme si sami řekli
        /// (viz _lastDesiredHeight) - nikdy podle aktuálního
        /// Console.WindowHeight, viz komentář u pole výše. Vrací true, pokud
        /// k reálné změně došlo - volající pak musí znovu překreslit i
        /// hlavičku (RenderDashboard), protože změna velikosti bufferu obsah
        /// smaže.
        /// </summary>
        private static bool EnsureWindowHeight(int desiredHeight)
        {
            desiredHeight = Math.Clamp(desiredHeight, 10, 60);
            if (desiredHeight == _lastDesiredHeight) return false;
            _lastDesiredHeight = desiredHeight;

            try
            {
                Console.SetWindowSize(1, 1);
                Console.SetBufferSize(ConsoleWidth, desiredHeight);
                Console.SetWindowSize(ConsoleWidth, desiredHeight);
                Console.Clear();
            }
            catch { }

            return true;
        }

        private static void RenderMetersOnly()
        {
            // Obsah VU metrů má vždycky přesně 7 řádků (L,R,H,S,V,Reg,Akt) -
            // na rozdíl od MIDI osnov se nemění podle souboru.
            const int contentRows = 7;
            if (EnsureWindowHeight(_contentStartRow + contentRows))
            {
                RenderDashboard(); // velikost se změnila -> hlavička je pryč, překreslit
            }

            Console.SetCursorPosition(0, _contentStartRow);

            const int barWidth = 14;
            string barL = AudioMeter.RenderBar(_leftDb, barWidth);
            string barR = AudioMeter.RenderBar(_rightDb, barWidth);

            Console.Write(" L:[");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(barL);
            Console.ResetColor();
            Console.WriteLine(FitWidth($"]{_leftDb,5:F1}dB", ContentWidth - 4 - barWidth));

            Console.Write(" R:[");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(barR);
            Console.ResetColor();
            Console.WriteLine(FitWidth($"]{_rightDb,5:F1}dB", ContentWidth - 4 - barWidth));

            // --- Tři pásma syntezátoru (hloubky/středy/výšky) ---
            // Stejný formát pruhu jako u L/R výše, jen jiná barva (azurová),
            // ať se to na první pohled odliší - jinak identický rozsah/škálování.
            string barBass = AudioMeter.RenderBar(_bassDb, barWidth);
            string barMid = AudioMeter.RenderBar(_midDb, barWidth);
            string barTreble = AudioMeter.RenderBar(_trebleDb, barWidth);

            Console.Write(" H:[");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(barBass);
            Console.ResetColor();
            Console.WriteLine(FitWidth($"]{_bassDb,5:F1}dB", ContentWidth - 4 - barWidth));

            Console.Write(" S:[");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(barMid);
            Console.ResetColor();
            Console.WriteLine(FitWidth($"]{_midDb,5:F1}dB", ContentWidth - 4 - barWidth));

            Console.Write(" V:[");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(barTreble);
            Console.ResetColor();
            Console.WriteLine(FitWidth($"]{_trebleDb,5:F1}dB", ContentWidth - 4 - barWidth));

            // --- Zadávání čísla rejstříku + přehled aktivních rejstříků ---
            Console.Write(" Reg:[");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(_registerInputBuffer.ToString().PadRight(3));
            Console.ResetColor();
            Console.WriteLine(FitWidth("] Ent/Esc/Bksp", ContentWidth - 6 - 3));

            Console.Write(" Akt: ");
            var active = App.OrganEngine?.ActiveRegisters;
            if (active != null && active.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(FitWidth(string.Join(",", active), ContentWidth - 6));
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(FitWidth("(žádný)", ContentWidth - 6));
            }
        }

        private static void RenderMidiStaffOnly()
        {
            // Jednoduchý náhled osnov - jeden řádek pro každou notovou osnovu
            // (kanál), kterou soubor používá, řádky pod sebou. Osnovy se
            // zobrazují TRVALE (nemizí, když zrovna nic nehrají) - jen se jim
            // mění obsah podle právě znějících not. Není to grafická notace,
            // jen názvy právě znějících not seřazené od nejnižší po nejvyšší.
            int[] usedChannels;
            (int Channel, int Note)[] activeNotesSnapshot;
            string? errorMessage;
            lock (_midiNotesLock)
            {
                usedChannels = _midiUsedChannels;
                activeNotesSnapshot = _midiActiveNotes.ToArray();
                errorMessage = _midiErrorMessage;
            }

            if (errorMessage != null)
            {
                // Soubor se nepodařilo načíst/přehrát - zůstáváme stát na něm
                // (viz GmPianoMidiPlayer.PlaybackFailed) a ukážeme proč, ať se
                // dá diagnostikovat, i kdyby v adresáři bylo víc vadných
                // souborů za sebou.
                var errorLines = WrapPath(errorMessage, ContentWidth - 1);
                if (EnsureWindowHeight(_contentStartRow + 1 + errorLines.Count))
                {
                    RenderDashboard();
                }

                Console.SetCursorPosition(0, _contentStartRow);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(FitWidth(" CHYBA přehrávání:", ContentWidth));
                foreach (string line in errorLines)
                {
                    Console.WriteLine(FitWidth($" {line}", ContentWidth));
                }
                Console.ResetColor();
                return;
            }

            // Kolik řádků bude potřeba - zjistí se PŘEDEM, ať se dá okno
            // přizpůsobit ještě před vlastním vypisováním (jinak by se
            // muselo kreslit dvakrát).
            string? seekDiagnostic = _midiPlayer.LastSeekDiagnostic;
            int contentRows = Math.Max(usedChannels.Length, 1) + (string.IsNullOrEmpty(seekDiagnostic) ? 0 : 1);
            if (EnsureWindowHeight(_contentStartRow + contentRows))
            {
                RenderDashboard();
            }

            Console.SetCursorPosition(0, _contentStartRow);

            var notesByChannel = activeNotesSnapshot
                .GroupBy(n => n.Channel)
                .ToDictionary(g => g.Key, g => g.Select(n => n.Note).OrderBy(n => n).ToArray());

            // Kompaktní popisek kanálu - "C01".."C16", bicí kanál (10) jako "D10" -
            // na 30 znaků širokém řádku není místo na dřívější "Ch01"/"Ch10[DRUM]".
            foreach (int channel in usedChannels)
            {
                string channelLabel = channel == 9 ? "D10" : $"C{channel + 1:D2}";
                string notes = notesByChannel.TryGetValue(channel, out var noteNumbers)
                    ? string.Join(" ", noteNumbers.Select(NoteNumberToName))
                    : "";

                string label = $" {channelLabel}:";
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(label);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(FitWidth(notes, ContentWidth - label.Length));
                Console.ResetColor();
            }

            if (usedChannels.Length == 0)
            {
                Console.WriteLine(FitWidth(" (osnovy zatím nerozpoznané)", ContentWidth));
            }

            // Diagnostika převíjení (ne fatální chyba, jen info k ladění) -
            // vypisujeme ji sem místo jen Debug.WriteLine, protože to je
            // v Release buildu jinak úplně neviditelné.
            if (!string.IsNullOrEmpty(seekDiagnostic))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(FitWidth($" [Seek] {seekDiagnostic}", ContentWidth));
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Převede MIDI číslo noty na běžný název (např. 60 -&gt; "C4", 69 -&gt; "A4").
        /// Střední C (MIDI 60) je podle konvence C4.
        /// </summary>
        private static string NoteNumberToName(int noteNumber)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int octave = (noteNumber / 12) - 1;
            string name = names[((noteNumber % 12) + 12) % 12];
            return $"{name}{octave}";
        }
    }
}
