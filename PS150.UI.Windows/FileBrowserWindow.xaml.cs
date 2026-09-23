using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PS150.Core;

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
        // Pevný počet sloupců mřížky - odpovídá dřívějšímu ContentWidth
        // (ConsoleWidth - 1) z VgaEngine. Ta "-1" rezerva tam byla čistě
        // kvůli console bugu; tady žádné skutečné zalamování řádků
        // nehrozí, ale ponechává se stejná šířka, ať zůstane platná
        // veškerá stará matematika odvozená od ní (FitWidth, WrapPath,
        // pozice VU pruhů...).
        private const int Cols = 29;

        // Řádek s transportními tlačítky - o 1 níž než v původním
        // VgaEngine, protože tady už není žádný ručně kreslený "fake"
        // title bar na řádku 0 (to teď dělá nativní okno).
        private const int TransportButtonsRow = 2;

        private int _contentStartRow = 6;
        private int _lastDesiredRows = -1;

        private readonly TextGridControl _grid;
        private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

        private readonly DirectoryNavigator _navigator = new();
        private readonly AudioPlayer _audioPlayer = new();
        private AppSettings _settings = AppSettings.Load();

        // Přehrávač .mid souborů na PC (natvrdo klavír přes Microsoft GS
        // Wavetable Synth) - viz GmPianoMidiPlayer.cs. Úmyslně NEpoužívá
        // PS150.Core.
        private readonly GmPianoMidiPlayer _midiPlayer = new();

        private readonly object _midiNotesLock = new object();
        private readonly HashSet<(int Channel, int Note)> _midiActiveNotes = new();
        private int[] _midiUsedChannels = Array.Empty<int>();
        private bool _midiFinishedNaturally = false;
        private string? _midiErrorMessage = null;

        private double _leftDb = -120.0;
        private double _rightDb = -120.0;
        private double _bassDb = -120.0;
        private double _midDb = -120.0;
        private double _trebleDb = -120.0;
        private int _volume = 80;
        private bool _isPaused = false;
        private bool _isMidiMode = false;

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

            _grid = new TextGridControl(Cols, 20);
            Canvas.SetLeft(_grid, 0);
            Canvas.SetTop(_grid, 0);
            RootCanvas.Children.Add(_grid);
            RootCanvas.Width = _grid.Cols * _grid.CellWidth;
            RootCanvas.Height = _grid.Rows * _grid.CellHeight;

            AddTransportButtons();
            WireMidiPlayerEvents();

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
            AddTransportButton(1, 4, (s, e) => _audioPlayer.Seek(-5.0));
            AddTransportButton(6, 8, (s, e) => { if (_isPaused) TogglePlayPause(); RenderDashboard(); });
            AddTransportButton(10, 12, (s, e) => { if (!_isPaused) TogglePlayPause(); RenderDashboard(); });
            AddTransportButton(14, 17, (s, e) => _audioPlayer.Seek(5.0));
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
            Canvas.SetTop(button, TransportButtonsRow * _grid.CellHeight);
            RootCanvas.Children.Add(button);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreWindowPosition();
            StartWithPendingFile();
        }

        private void RestoreWindowPosition()
        {
            if (!_settings.WindowX.HasValue || !_settings.WindowY.HasValue) return;

            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = SystemParameters.VirtualScreenWidth;
            double vHeight = SystemParameters.VirtualScreenHeight;

            // Necháváme aspoň kousek okna viditelný, ať je za co ho příště
            // chytit - stejná kontrola jako dřív ve VgaEngine.Run().
            const int minVisible = 50;
            bool withinBounds =
                _settings.WindowX.Value >= vLeft - minVisible &&
                _settings.WindowX.Value <= vLeft + vWidth - minVisible &&
                _settings.WindowY.Value >= vTop &&
                _settings.WindowY.Value <= vTop + vHeight - minVisible;

            if (withinBounds)
            {
                Left = _settings.WindowX.Value;
                Top = _settings.WindowY.Value;
            }
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _tickTimer.Stop();

            _settings.WindowX = (int)Left;
            _settings.WindowY = (int)Top;

            string? lastFile = HandoffFile ?? _navigator.CurrentFile;
            if (!string.IsNullOrEmpty(lastFile))
            {
                _settings.LastFilePath = lastFile;
                _settings.LastFolderPath = Path.GetDirectoryName(lastFile);
            }
            _settings.Save();

            _audioPlayer.Stop();
            _midiPlayer.Stop();
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
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

            RenderDashboard();

            if (!_isMidiMode)
            {
                var (peakL, peakR) = _audioPlayer.ReadPeakLevels();

                float organPeak = App.OrganEngine?.ReadPeak() ?? 0f;
                float combinedL = Math.Max(peakL, organPeak);
                float combinedR = Math.Max(peakR, organPeak);

                _leftDb = AudioMeter.LinearToDecibels(combinedL);
                _rightDb = AudioMeter.LinearToDecibels(combinedR);

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
            _volume = Math.Clamp(_volume + delta, 0, 100);
            _audioPlayer.Volume = _volume / 100.0f;
            _midiPlayer.SetVolume(_volume);

            _settings.Volume = _volume;
            _settings.Save();
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
                    _ = _midiPlayer.PlayAsync(currentFile);
                    _midiPlayer.SetVolume(_volume);
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

                case Key.Up:
                    ChangeVolume(5);
                    break;

                case Key.Down:
                    ChangeVolume(-5);
                    break;
            }
            return true;
        }

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
                if (breakAt <= pos)
                {
                    breakAt = pos + width - 1;
                }
                else
                {
                    breakAt++;
                }

                lines.Add(text.Substring(pos, breakAt - pos));
                pos = breakAt;
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        private static string FitWidth(string text, int width)
        {
            if (width <= 0) return "";
            if (text.Length <= width) return text.PadRight(width);
            return width == 1 ? text.Substring(0, 1) : text.Substring(0, width - 1) + "…";
        }

        private bool EnsureRows(int desiredRows)
        {
            desiredRows = Math.Clamp(desiredRows, 10, 60);
            if (desiredRows == _lastDesiredRows) return false;
            _lastDesiredRows = desiredRows;

            _grid.SetRows(desiredRows);
            RootCanvas.Width = _grid.Cols * _grid.CellWidth;
            RootCanvas.Height = _grid.Rows * _grid.CellHeight;
            return true;
        }

        private void RenderDashboard()
        {
            _grid.SetCursorPosition(0, 0);

            string currentFile = _navigator.CurrentFile ?? "No file loaded";
            string fileName = Path.GetFileName(currentFile);
            string folderPath = Path.GetDirectoryName(currentFile) ?? "";
            string fullPath = $"{folderPath}\\{fileName}";

            TimeSpan current = _isMidiMode ? _midiPlayer.CurrentTime : _audioPlayer.CurrentTime;
            TimeSpan total = _isMidiMode ? _midiPlayer.TotalTime : _audioPlayer.TotalTime;
            string timeStr = $"{current:mm\\:ss}/{total:mm\\:ss}";

            string separator = new string('=', Cols);
            string thinSeparator = new string('-', Cols);

            // --- Řádek 0: oddělovač ---
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.WriteLine(separator);

            // --- Řádek 1: hlasitost + čas ---
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.WriteLine($" Vol:{_volume,3}% [{timeStr}]".PadRight(Cols));

            // --- Řádek 2: transportní tlačítka (souřadnice viz AddTransportButtons) ---
            _grid.ForegroundColor = ConsoleColor.Yellow;
            _grid.Write(" [<<] ");

            bool isActuallyPlaying = _isMidiMode ? !_midiPlayer.IsPaused : _audioPlayer.IsPlaying;

            if (!_isPaused && isActuallyPlaying)
            {
                _grid.BackgroundColor = ConsoleColor.Green;
                _grid.ForegroundColor = ConsoleColor.Black;
                _grid.Write("[►]");
                _grid.ResetColor();
            }
            else
            {
                _grid.ForegroundColor = ConsoleColor.DarkGray;
                _grid.Write("[►]");
            }
            _grid.Write(" ");

            if (_isPaused || !isActuallyPlaying)
            {
                _grid.BackgroundColor = ConsoleColor.DarkRed;
                _grid.ForegroundColor = ConsoleColor.White;
                _grid.Write("[▄]");
                _grid.ResetColor();
            }
            else
            {
                _grid.ForegroundColor = ConsoleColor.DarkGray;
                _grid.Write("[▄]");
            }
            _grid.Write(" ");

            _grid.ForegroundColor = ConsoleColor.Yellow;
            _grid.Write("[>>] ");
            _grid.ForegroundColor = ConsoleColor.Magenta;
            _grid.Write("♪♫");
            _grid.ResetColor();
            _grid.WriteLine();

            // --- Řádek 3: oddělovač ---
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.WriteLine(separator);
            _grid.ResetColor();

            // --- Cesta k souboru, zalomená na šířku okna ---
            var pathLines = WrapPath(fullPath, Cols - 1);
            foreach (string line in pathLines)
            {
                _grid.WriteLine($" {line}".PadRight(Cols));
            }

            _grid.ForegroundColor = ConsoleColor.DarkGray;
            _grid.WriteLine(thinSeparator);
            _grid.ResetColor();

            // Řádky: sep(1) + vol(1) + transport(1) + sep(1) + cesta(pathLines.Count) + tenký oddělovač(1)
            _contentStartRow = 4 + pathLines.Count + 1;
        }

        private void RenderMetersOnly()
        {
            const int contentRows = 7;
            if (EnsureRows(_contentStartRow + contentRows))
            {
                RenderDashboard();
            }

            _grid.SetCursorPosition(0, _contentStartRow);

            const int barWidth = 14;
            string barL = AudioMeter.RenderBar(_leftDb, barWidth);
            string barR = AudioMeter.RenderBar(_rightDb, barWidth);

            _grid.Write(" L:[");
            _grid.ForegroundColor = ConsoleColor.Green;
            _grid.Write(barL);
            _grid.ResetColor();
            _grid.WriteLine(FitWidth($"]{_leftDb,5:F1}dB", Cols - 4 - barWidth));

            _grid.Write(" R:[");
            _grid.ForegroundColor = ConsoleColor.Green;
            _grid.Write(barR);
            _grid.ResetColor();
            _grid.WriteLine(FitWidth($"]{_rightDb,5:F1}dB", Cols - 4 - barWidth));

            string barBass = AudioMeter.RenderBar(_bassDb, barWidth);
            string barMid = AudioMeter.RenderBar(_midDb, barWidth);
            string barTreble = AudioMeter.RenderBar(_trebleDb, barWidth);

            _grid.Write(" H:[");
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(barBass);
            _grid.ResetColor();
            _grid.WriteLine(FitWidth($"]{_bassDb,5:F1}dB", Cols - 4 - barWidth));

            _grid.Write(" S:[");
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(barMid);
            _grid.ResetColor();
            _grid.WriteLine(FitWidth($"]{_midDb,5:F1}dB", Cols - 4 - barWidth));

            _grid.Write(" V:[");
            _grid.ForegroundColor = ConsoleColor.Cyan;
            _grid.Write(barTreble);
            _grid.ResetColor();
            _grid.WriteLine(FitWidth($"]{_trebleDb,5:F1}dB", Cols - 4 - barWidth));

            _grid.Write(" Reg:[");
            _grid.ForegroundColor = ConsoleColor.Red;
            _grid.Write(_registerInputBuffer.ToString().PadRight(3));
            _grid.ResetColor();
            _grid.WriteLine(FitWidth("] Ent/Esc/Bksp", Cols - 6 - 3));

            _grid.Write(" Akt: ");
            var active = App.OrganEngine?.ActiveRegisters;
            if (active != null && active.Count > 0)
            {
                _grid.ForegroundColor = ConsoleColor.Green;
                _grid.WriteLine(FitWidth(string.Join(",", active), Cols - 6));
                _grid.ResetColor();
            }
            else
            {
                _grid.WriteLine(FitWidth("(žádný)", Cols - 6));
            }
        }

        private void RenderMidiStaffOnly()
        {
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
                var errorLines = WrapPath(errorMessage, Cols - 1);
                if (EnsureRows(_contentStartRow + 1 + errorLines.Count))
                {
                    RenderDashboard();
                }

                _grid.SetCursorPosition(0, _contentStartRow);
                _grid.ForegroundColor = ConsoleColor.Red;
                _grid.WriteLine(FitWidth(" CHYBA přehrávání:", Cols));
                foreach (string line in errorLines)
                {
                    _grid.WriteLine(FitWidth($" {line}", Cols));
                }
                _grid.ResetColor();
                return;
            }

            string? seekDiagnostic = _midiPlayer.LastSeekDiagnostic;
            int contentRows = Math.Max(usedChannels.Length, 1) + (string.IsNullOrEmpty(seekDiagnostic) ? 0 : 1);
            if (EnsureRows(_contentStartRow + contentRows))
            {
                RenderDashboard();
            }

            _grid.SetCursorPosition(0, _contentStartRow);

            var notesByChannel = activeNotesSnapshot
                .GroupBy(n => n.Channel)
                .ToDictionary(g => g.Key, g => g.Select(n => n.Note).OrderBy(n => n).ToArray());

            foreach (int channel in usedChannels)
            {
                string channelLabel = channel == 9 ? "D10" : $"C{channel + 1:D2}";
                string notes = notesByChannel.TryGetValue(channel, out var noteNumbers)
                    ? string.Join(" ", noteNumbers.Select(NoteNumberToName))
                    : "";

                string label = $" {channelLabel}:";
                _grid.ForegroundColor = ConsoleColor.White;
                _grid.Write(label);
                _grid.ForegroundColor = ConsoleColor.Green;
                _grid.WriteLine(FitWidth(notes, Cols - label.Length));
                _grid.ResetColor();
            }

            if (usedChannels.Length == 0)
            {
                _grid.WriteLine(FitWidth(" (osnovy zatím nerozpoznané)", Cols));
            }

            if (!string.IsNullOrEmpty(seekDiagnostic))
            {
                _grid.ForegroundColor = ConsoleColor.Yellow;
                _grid.WriteLine(FitWidth($" [Seek] {seekDiagnostic}", Cols));
                _grid.ResetColor();
            }
        }

        private static string NoteNumberToName(int noteNumber)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int octave = (noteNumber / 12) - 1;
            string name = names[((noteNumber % 12) + 12) % 12];
            return $"{name}{octave}";
        }
    }
}
