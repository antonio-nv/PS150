using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PS150.UI.Windows
{
    /// <summary>
    /// Samostatné okno položené NAD video oknem (MainWindow) - řeší WPF
    /// "airspace": nativní HWND, do kterého VLC kreslí obraz, se vždycky
    /// vykreslí nad čistě WPF obsahem bez ohledu na pořadí v XAML, takže
    /// panely uvnitř téhož okna jako VideoView by prosvítaly jen tam, kde
    /// zrovna obraz není. Samostatné okno tenhle problém úplně obchází.
    ///
    /// Nad videem zůstává díky vztahu Owner (nastavuje MainWindow při
    /// vytvoření) - NE přes Topmost, to by drželo overlay navrchu úplně
    /// nade vším na obrazovce a bránilo by to přepnout se na jiný program.
    /// Owner zajišťuje jen "nad svým vlastníkem", nic víc - celá dvojice
    /// (video+overlay) se dá zakrýt jiným oknem normálně.
    ///
    /// Většinu času je "proklikávací" (WS_EX_TRANSPARENT) - myš i klik
    /// propadají až na MainWindow pod tímhle. Neprůhledné/klikatelné se
    /// stává jen ve chvíli, kdy je vidět šoupátko (SeekOverlay), ať na něj
    /// jde myší sáhnout. Nikdy nepřebírá klávesový fokus (WS_EX_NOACTIVATE +
    /// ShowActivated="False") - ten musí zůstat pořád u MainWindow.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        /// <summary>Vyvoláno po puštění šoupátka - podíl 0.0-1.0 celkové délky.</summary>
        public event Action<double>? SeekRequested;

        private readonly DispatcherTimer _fileInfoHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _seekOverlayHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _isUserDraggingSeekBar = false;
        private bool _isClickThrough = true;

        public OverlayWindow()
        {
            InitializeComponent();

            // ŽÁDNÉ WindowState.Maximized zde - WPF to spolu s
            // ShowActivated="False" zakazuje (vyhazuje
            // InvalidOperationException hned při Show()). Místo toho se
            // okno ručně roztáhne na rozměry hlavní obrazovky - funkčně
            // stejný výsledek, bez zakázané kombinace.
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            _fileInfoHideTimer.Tick += (s, e) =>
            {
                _fileInfoHideTimer.Stop();
                HideOverlay(FileInfoOverlay);
            };
            _seekOverlayHideTimer.Tick += (s, e) =>
            {
                _seekOverlayHideTimer.Stop();
                HideOverlay(SeekOverlay);
                SetClickThrough(true);
            };

            SourceInitialized += (s, e) =>
            {
                SetNoActivate();
                SetClickThrough(true);
            };
        }

        /// <summary>Ukáže cestu+jméno souboru na 2s (nový soubor).</summary>
        public void ShowFileInfo(string path)
        {
            FileInfoText.Text = path;
            ShowOverlay(FileInfoOverlay, _fileInfoHideTimer);
        }

        /// <summary>Ukáže hlasitost jako obyčejné číslo na 2s (šipky / kolečko myši).</summary>
        public void ShowVolume(int percent)
        {
            VolumeText.Text = $"Hlasitost: {percent} %";
            ShowSeekOverlay();
        }

        /// <summary>Volá MainWindow při pohybu myší - ukáže spodní panel.</summary>
        public void NotifyMouseActivity() => ShowSeekOverlay();

        /// <summary>Volá MainWindow po posunu šipkami - ukáže panel a rovnou i nový čas.</summary>
        public void ShowSeekOverlayForKeySeek(long currentMs, long totalMs)
        {
            UpdateTime(currentMs, totalMs);
            ShowSeekOverlay();
        }

        /// <summary>Volá MainWindow periodicky (viz _seekOverlayUpdateTimer) - bez ohledu na to, jestli je panel zrovna vidět, neškodí.</summary>
        public void UpdateTime(long currentMs, long totalMs)
        {
            CurrentTimeText.Text = FormatTime(currentMs);
            TotalTimeText.Text = FormatTime(totalMs);
            if (totalMs > 0 && !_isUserDraggingSeekBar)
            {
                SeekSlider.Value = (double)currentMs / totalMs * SeekSlider.Maximum;
            }
        }

        private void ShowSeekOverlay()
        {
            ShowOverlay(SeekOverlay, _seekOverlayHideTimer);
            SetClickThrough(false); // dokud je vidět, musí jít na šoupátko myší sáhnout
        }

        private static void ShowOverlay(FrameworkElement overlay, DispatcherTimer hideTimer)
        {
            overlay.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(overlay.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
            overlay.BeginAnimation(OpacityProperty, fadeIn);
            hideTimer.Stop();
            hideTimer.Start();
        }

        private static void HideOverlay(FrameworkElement overlay)
        {
            var fadeOut = new DoubleAnimation(overlay.Opacity, 0.0, TimeSpan.FromMilliseconds(400));
            fadeOut.Completed += (s, e) => overlay.Visibility = Visibility.Collapsed;
            overlay.BeginAnimation(OpacityProperty, fadeOut);
        }

        private static string FormatTime(long milliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
        }

        // Dokud je okno "proklikávací" (viz SetClickThrough), tenhle handler
        // se vůbec nezavolá - myš propadá rovnou na MainWindow. Zavolá se,
        // až jakmile se panel jednou ukáže a proklikávání se dočasně vypne -
        // pak pohyb myši kdekoliv (i mimo samotný panel) drží 2s odpočet
        // naživu, ať panel nezmizí zpod pohybující se myši.
        private void Grid_MouseMove(object sender, MouseEventArgs e) => ShowSeekOverlay();

        private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isUserDraggingSeekBar = true;
        }

        private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isUserDraggingSeekBar = false;
            SeekRequested?.Invoke(SeekSlider.Value / SeekSlider.Maximum);
            ShowSeekOverlay();
        }

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUserDraggingSeekBar) return;
            ShowSeekOverlay();
        }

        // --- Proklikávací / neaktivující okno (Win32) ---

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_LAYERED = 0x00080000;

        /// <summary>Okno nikdy nezíská klávesový fokus/aktivaci - ta musí zůstat u MainWindow.</summary>
        private void SetNoActivate()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        }

        /// <summary>
        /// enabled=true: myš propadá skrz (výchozí stav, MainWindow dostává vše).
        /// enabled=false: okno myš přebírá (jen dokud je vidět šoupátko).
        /// </summary>
        private void SetClickThrough(bool enabled)
        {
            if (enabled == _isClickThrough) return;
            _isClickThrough = enabled;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle = enabled ? (exStyle | WS_EX_TRANSPARENT) : (exStyle & ~WS_EX_TRANSPARENT);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }
    }
}
