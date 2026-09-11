using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PS150.Core;
using LibVLCSharp.Shared;

namespace PS150.UI.Windows
{
    public partial class MainWindow : Window
    {
        private DirectoryNavigator _navigator = new();

        // VLC objekty
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;

        // Samostatné okno s panely (cesta, čas, šoupátko, hlasitost) - viz
        // OverlayWindow.xaml.cs pro vysvětlení, proč to nejde jako obsah
        // přímo v tomhle okně (WPF "airspace" - video HWND by ho překrývalo).
        private readonly OverlayWindow _overlay = new();

        private readonly DispatcherTimer _seekOverlayUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

        public MainWindow()
        {
            InitializeComponent();

            // Inicializace samotného jádra LibVLC
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            // Propojení VLC s WPF prvkem v XAML
            VideoViewer.MediaPlayer = _mediaPlayer;

            // Vždycky fullscreen, bez přepínání do okna - dřívější zvyk
            // "spustit v okně, pak přepnout na fullscreen" je dnes zbytečný
            // krok navíc, tak zůstává rovnou jen u tohohle.
            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;

            _overlay.Show();
            _overlay.SeekRequested += fraction =>
            {
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(fraction * _mediaPlayer.Length));
                }
            };

            // Dvě okna nastavená jako Topmost (MainWindow i _overlay) mají
            // vzájemné pořadí dané tím, které bylo naposledy "nahoře" - a
            // aktivace MainWindow (třeba po Alt+Tab zpátky) by ho v tomhle
            // pořadí mohla vytáhnout před overlay. Při každé aktivaci proto
            // overlay znovu protlačíme na vrch (přepnutím Topmost off/on je
            // to standardní trik, jak WPF donutit poslat SetWindowPos znovu).
            this.Activated += (s, e) =>
            {
                _overlay.Topmost = false;
                _overlay.Topmost = true;
            };

            _seekOverlayUpdateTimer.Tick += (s, e) =>
            {
                if (_mediaPlayer == null) return;
                _overlay.UpdateTime(_mediaPlayer.Time, _mediaPlayer.Length);
            };
            _seekOverlayUpdateTimer.Start();
        }

        public void PlayFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                _navigator.LoadDirectory(filePath);
                StartPlayingCurrentFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chyba načítání videa: {ex.Message}");
            }
        }

        private void StartPlayingCurrentFile()
        {
            try
            {
                string? currentFile = _navigator.CurrentFile;
                if (currentFile == null || !File.Exists(currentFile) || _libVLC == null || _mediaPlayer == null)
                    return;

                // Nejdřív zastavíme předchozí přehrávání - přímé spuštění nového
                // Media bez Stop() (hlavně při rychlém opakovaném PageUp/PageDown)
                // občas způsobovalo pád LibVLC (AccessViolationException uvnitř
                // nativní knihovny) a aplikace šla zavřít jen přes Alt+F4.
                _mediaPlayer.Stop();

                using var media = new Media(_libVLC, new Uri(currentFile));
                _mediaPlayer.Play(media);

                _overlay.ShowFileInfo(currentFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chyba při přepínání videa: {ex.Message}");
            }
        }

        private void MainWindow_MouseMove(object sender, MouseEventArgs e)
        {
            _overlay.NotifyMouseActivity();
        }

        private void MainWindow_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_mediaPlayer == null) return;

            // Hlasitost ve VLC je 0 až 100
            _mediaPlayer.Volume = e.Delta > 0
                ? Math.Min(100, _mediaPlayer.Volume + 5)
                : Math.Max(0, _mediaPlayer.Volume - 5);

            _overlay.ShowVolume(_mediaPlayer.Volume);
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (_mediaPlayer == null) return;

            // Alt + F4
            if (e.Key == Key.System && e.SystemKey == Key.F4)
            {
                Close();
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;

                case Key.Space:
                    if (_mediaPlayer.IsPlaying)
                    {
                        _mediaPlayer.Pause();
                    }
                    else
                    {
                        _mediaPlayer.Play();
                    }
                    e.Handled = true;
                    break;

                // Hlasitost šipkami nahoru/dolů - jen číslo v overlay, žádné druhé šoupátko
                case Key.Up:
                    _mediaPlayer.Volume = Math.Min(100, _mediaPlayer.Volume + 5);
                    _overlay.ShowVolume(_mediaPlayer.Volume);
                    e.Handled = true;
                    break;

                case Key.Down:
                    _mediaPlayer.Volume = Math.Max(0, _mediaPlayer.Volume - 5);
                    _overlay.ShowVolume(_mediaPlayer.Volume);
                    e.Handled = true;
                    break;

                // BLESKOVÉ PŘEVÍJENÍ ŠIPKAMI (+/- 5000 ms)
                case Key.Right:
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(_mediaPlayer.Time + 5000));
                    _overlay.ShowSeekOverlayForKeySeek(_mediaPlayer.Time, _mediaPlayer.Length);
                    e.Handled = true;
                    break;

                case Key.Left:
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time - 5000)));
                    _overlay.ShowSeekOverlayForKeySeek(_mediaPlayer.Time, _mediaPlayer.Length);
                    e.Handled = true;
                    break;

                // Přeskakování souborů
                case Key.PageDown:
                    try
                    {
                        _navigator.GetNextFile();
                        StartPlayingCurrentFile();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Chyba při přechodu na další soubor: {ex.Message}");
                    }
                    e.Handled = true;
                    break;

                case Key.PageUp:
                    try
                    {
                        _navigator.GetPreviousFile();
                        StartPlayingCurrentFile();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Chyba při přechodu na předchozí soubor: {ex.Message}");
                    }
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Úklid paměti při zavření okna
            _seekOverlayUpdateTimer.Stop();
            _overlay.Close();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();

            base.OnClosed(e);
        }
    }
}
