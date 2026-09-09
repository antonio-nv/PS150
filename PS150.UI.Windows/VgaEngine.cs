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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_PROCESSED_INPUT = 0x0001;
        private const uint ENABLE_LINE_INPUT = 0x0002;
        private const uint ENABLE_ECHO_INPUT = 0x0004;
        private const uint ENABLE_MOUSE_INPUT = 0x0010;
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
        private static extern bool PeekConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

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
            public bool bKeyDown;
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

        public static void Run(string filePath)
        {
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
            if (hwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hwnd);
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;
            Console.Title = "PS150 - VGA Console Engine";

            try
            {
                Console.SetBufferSize(Console.WindowWidth, Console.WindowHeight);
            }
            catch { }

            // 2. Nastavení režimu vstupu konzole
            IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
            if (GetConsoleMode(hInput, out uint mode))
            {
                mode |= ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS;
                // Vypneme řádkový vstup a echo, aby klávesnice reagovala okamžitě
                mode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
                SetConsoleMode(hInput, mode);
            }

            if (File.Exists(filePath))
            {
                _navigator.LoadDirectory(filePath);
                UpdateFileTypeState();
            }

            RenderDashboard();

            bool running = true;
            INPUT_RECORD[] recordBuffer = new INPUT_RECORD[1];

            while (running)
            {
                // A) Čtení klávesnice přes standardní .NET Console API (100% spolehlivé)
                while (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(true);
                    running = HandleInput(keyInfo.Key);
                    RenderDashboard();
                    if (!running) break;
                }

                if (!running) break;

                // B) Zpracování myši (Kolečko + Kliky)
                PeekConsoleInput(hInput, recordBuffer, 1, out uint eventsRead);
                if (eventsRead > 0)
                {
                    ReadConsoleInput(hInput, recordBuffer, 1, out _);
                    var record = recordBuffer[0];

                    // Kolečko myši
                    if (record.EventType == MOUSE_EVENT && record.MouseEvent.dwEventFlags == MOUSE_WHEELED)
                    {
                        int scrollDelta = (int)record.MouseEvent.dwButtonState >> 16;
                        ChangeVolume(scrollDelta > 0 ? 5 : -5);
                        RenderDashboard();
                    }
                    // Kliknutí myší (Levé tlačítko)
                    else if (record.EventType == MOUSE_EVENT && record.MouseEvent.dwEventFlags == 0)
                    {
                        if ((record.MouseEvent.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                        {
                            HandleMouseClick(record.MouseEvent.dwMousePosition.X, record.MouseEvent.dwMousePosition.Y);
                            RenderDashboard();
                        }
                    }
                }

                // C) Kontrola konce skladby -> AUTOMATICKÝ POSUN NA DALŠÍ SOUBOR
                if (!_isMidiMode && !_isPaused && _audioPlayer.TotalTime > TimeSpan.Zero)
                {
                    if (_audioPlayer.CurrentTime >= _audioPlayer.TotalTime - TimeSpan.FromMilliseconds(300))
                    {
                        _navigator.GetNextFile();
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
                        _navigator.GetNextFile();
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

            _audioPlayer.Dispose();
            _midiPlayer.Stop();
            Console.CursorVisible = true;
        }

        private static void HandleMouseClick(short x, short y)
        {
            if (y == 1)
            {
                if (x >= 40 && x <= 43)      // [<<]
                {
                    _audioPlayer.Seek(-5.0);
                }
                else if (x >= 45 && x <= 47) // [►]
                {
                    if (_isPaused) TogglePlayPause();
                }
                else if (x >= 49 && x <= 51) // [▄]
                {
                    if (!_isPaused) TogglePlayPause();
                }
                else if (x >= 53 && x <= 56) // [>>]
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

        private static void RenderDashboard()
        {
            Console.SetCursorPosition(0, 0);

            string currentFile = _navigator.CurrentFile ?? "No file loaded";
            string fileName = Path.GetFileName(currentFile);
            string folderPath = Path.GetDirectoryName(currentFile) ?? "";
            string modeLabel = _isMidiMode ? "MIDI PASS-THROUGH" : "AUDIO STREAM (WASAPI)";

            // Čas se čte z toho přehrávače, který právě opravdu hraje - u
            // audio souborů z _audioPlayer (odhad z WASAPI streamu), u MIDI
            // z _midiPlayer (přesný výpočet z dat souboru, viz komentář u
            // GmPianoMidiPlayer.TotalTime).
            TimeSpan current = _isMidiMode ? _midiPlayer.CurrentTime : _audioPlayer.CurrentTime;
            TimeSpan total = _isMidiMode ? _midiPlayer.TotalTime : _audioPlayer.TotalTime;
            string timeStr = $"{current:mm\\:ss} / {total:mm\\:ss}";

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=========================================================================================");

            Console.Write($" PS150 | Vol: {_volume,3}% | [{timeStr}] ");

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("[<<] ");

            // Stejný princip jako u času výše - "hraje teď" se ptáme toho
            // přehrávače, který je zrovna aktivní, ne vždycky _audioPlayer.
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

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();

            Console.WriteLine($" Mode: {modeLabel,-22}");
            Console.WriteLine("=========================================================================================");
            Console.ResetColor();

            Console.WriteLine($"  {folderPath}\\{fileName} ");
            Console.WriteLine("-----------------------------------------------------------------------------------------");
        }

        private static void RenderMetersOnly()
        {
            Console.SetCursorPosition(0, 7);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-120dB             -90db                 -60db                -30dB                 0dB");
            Console.ResetColor();

            string barL = AudioMeter.RenderBar(_leftDb, 80);
            string barR = AudioMeter.RenderBar(_rightDb, 80);

            Console.Write(" L: [");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(barL);
            Console.ResetColor();
            Console.WriteLine($"] {_leftDb,6:F1} dB");

            Console.Write(" R: [");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(barR);
            Console.ResetColor();
            Console.WriteLine($"] {_rightDb,6:F1} dB");

            // --- Tři pásma syntezátoru (hloubky/středy/výšky) ---
            // Stejná legenda i formát pruhu jako u L/R výše (viz řádek "-120dB...0dB").
            // Jiná barva (azurová) jen kvůli přehlednosti, ať se dá od L/R na první
            // pohled odlišit - jinak identický vzhled/rozsah/škálování.
            string barBass = AudioMeter.RenderBar(_bassDb, 80);
            string barMid = AudioMeter.RenderBar(_midDb, 80);
            string barTreble = AudioMeter.RenderBar(_trebleDb, 80);

            Console.Write(" H: [");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(barBass);
            Console.ResetColor();
            Console.WriteLine($"] {_bassDb,6:F1} dB");

            Console.Write(" S: [");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(barMid);
            Console.ResetColor();
            Console.WriteLine($"] {_midDb,6:F1} dB");

            Console.Write(" V: [");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(barTreble);
            Console.ResetColor();
            Console.WriteLine($"] {_trebleDb,6:F1} dB");

            // --- Zadávání čísla rejstříku + přehled aktivních rejstříků ---
            Console.Write(" Rejstřík č.: [");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(_registerInputBuffer.ToString().PadRight(3));
            Console.ResetColor();
            Console.WriteLine("]  (piš číslo, Enter = ON/OFF, Backspace = smaž, Esc = zruš)   ");

            Console.Write(" Aktivní rejstříky: ");
            var active = App.OrganEngine?.ActiveRegisters;
            if (active != null && active.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(string.Join(", ", active));
                Console.ResetColor();
                Console.WriteLine("                                                        ");
            }
            else
            {
                Console.WriteLine("(žádný)                                                        ");
            }
        }

        private static void RenderMidiStaffOnly()
        {
            Console.SetCursorPosition(0, 7);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-----------------------------------------------------------------------------------------");
            Console.ResetColor();

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
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" CHYBA při přehrávání tohoto souboru - zůstávám stát, další soubor se nespustí:");
                Console.WriteLine($" {errorMessage}".PadRight(90));
                Console.ResetColor();

                for (int i = 0; i < 8; i++)
                {
                    Console.WriteLine(new string(' ', 90));
                }
                return;
            }

            var notesByChannel = activeNotesSnapshot
                .GroupBy(n => n.Channel)
                .ToDictionary(g => g.Key, g => g.Select(n => n.Note).OrderBy(n => n).ToArray());

            int staffLines = 0;
            foreach (int channel in usedChannels)
            {
                string channelLabel = channel == 9 ? "Ch10[DRUM]" : $"Ch{channel + 1:D2}";
                string notes = notesByChannel.TryGetValue(channel, out var noteNumbers)
                    ? string.Join(" ", noteNumbers.Select(NoteNumberToName))
                    : "";

                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {channelLabel,-10}: ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(notes.PadRight(80));
                Console.ResetColor();
                staffLines++;
            }

            if (staffLines == 0)
            {
                Console.WriteLine(" (osnovy souboru zatím nejsou rozpoznané)                                              ");
                staffLines = 1;
            }

            // Diagnostika převíjení (ne fatální chyba, jen info k ladění) -
            // vypisujeme ji sem místo jen Debug.WriteLine, protože to je
            // v Release buildu jinak úplně neviditelné.
            string? seekDiagnostic = _midiPlayer.LastSeekDiagnostic;
            if (!string.IsNullOrEmpty(seekDiagnostic))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" [Převíjení] {seekDiagnostic}".PadRight(90));
                Console.ResetColor();
                staffLines++;
            }

            // Smažeme případný zbytek předchozích (delších) osnov, ať staré řádky
            // nezůstanou "viset" pod aktuálním výpisem po zmenšení počtu kanálů.
            for (int i = staffLines; i < 10; i++)
            {
                Console.WriteLine(new string(' ', 90));
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
