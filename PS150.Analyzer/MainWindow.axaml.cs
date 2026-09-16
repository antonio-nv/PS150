using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using PS150.Core;

namespace PS150.VideoEtAudio
{
    public partial class MainWindow : Window
    {
        private readonly DirectoryNavigator _navigator = new();

        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private bool _videoViewReady = false;
        private string? _pendingFile;

        public MainWindow()
        {
            InitializeComponent();

            // POZOR (Linux): libvlc musí být nainstalované v systému přes apt
            // (sudo apt install vlc libvlc-dev) - stejně jako u PS150.UI.Linux.
            // Na Windows to obstará balíček VideoLAN.LibVLC.Windows.
            LibVLCSharp.Shared.Core.Initialize();

            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            // DŮLEŽITÉ: LibVLC má ve výchozím nastavení VLASTNÍ vestavěné
            // ovládání klávesnicí/myší nad video plochou (F=fullscreen,
            // Esc, mezerník...) - určené pro situace, kdy LibVLC řídí celé
            // okno samo. To soutěží o tu samou klávesu s naším vlastním
            // KeyDown handlerem níž, a na Linuxu/X11 vyhrávalo LibVLC -
            // proto Escape/Alt+F4 nezabíraly. Vypnutím se klávesnice i myš
            // nechá zpracovat výhradně naší appkou.
            _mediaPlayer.EnableKeyInput = false;
            _mediaPlayer.EnableMouseInput = false;

            // DŮLEŽITÉ: "Opened" u okna je pořád MOC BRZY. Nativní "úchyt",
            // do kterého LibVLC kreslí obraz, vzniká uvnitř VideoView až ve
            // chvíli, kdy se ten konkrétní ovládací prvek připojí do
            // vizuálního stromu (AttachedToVisualTree) - a to může nastat
            // i PO Opened okna. Pokud LibVLC dostane Play() dřív, než tohle
            // proběhne, nemá kam kreslit a nouzově si otevře VLASTNÍ okno
            // ("VLC (Direct3D11 output)") - přesně tenhle bug popisuje i
            // vývojář LibVLC na jejich vlastním fóru.
            var videoView = this.FindControl<VideoView>("VideoViewer");
            if (videoView != null)
            {
                videoView.AttachedToVisualTree += (s, e) =>
                {
                    videoView.MediaPlayer = _mediaPlayer;
                    _videoViewReady = true;

                    // Pokud mezitím přišel požadavek na přehrání (z App.axaml.cs),
                    // ale VideoView ještě nebylo připravené, čekal na tuhle chvíli.
                    if (_pendingFile != null)
                    {
                        string file = _pendingFile;
                        _pendingFile = null;
                        PlayFile(file);
                    }
                };
            }

            // DŮLEŽITÉ: obyčejné "KeyDown +=" (bublající fáze) se u VideoView
            // nemusí vůbec dostat na řadu - ta v sobě hostí SKUTEČNÉ nativní
            // okénko (kvůli plynulému vykreslování videa) a klávesa mu občas
            // jde napřímo od Windows, ne přes Avalonii. Tunelová fáze
            // (shora dolů, PŘED tím, než se dostane k samotnému VideoView)
            // + handledEventsToo je odolnější varianta, proto se používá
            // jen tahle, ne obyčejné "KeyDown +=".
            AddHandler(KeyDownEvent, MainWindow_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // Explicitně držet fokus na okně, ne nechat ho "ukrást" VideoView.
            Opened += (s, e) => Focus();

            // Klikací tlačítko na zavření (spolehlivé i tehdy, když klávesnice
            // kvůli nativnímu video okénku nefunguje - viz komentář výše).
            Opened += (s, e) =>
            {
                var closeOverlay = new CloseButtonOverlay();
                closeOverlay.SetCloseAction(Close);
                closeOverlay.Show(this); // vlastnictví se v Avalonii nastavuje takhle, ne přes { Owner = this }

                // Obrazovka, na které TOHLE okno skutečně leží - ne
                // "primární" monitor obecně (relevantní při víc monitorech).
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen != null)
                {
                    closeOverlay.Position = new Avalonia.PixelPoint(
                        screen.WorkingArea.Right - (int)closeOverlay.Width - 12,
                        screen.WorkingArea.Y + 12);
                }
            };
        }

        /// <summary>Volá se z App.axaml.cs. Pokud VideoView ještě není připravené (viz AttachedToVisualTree výše), přehrání se odloží.</summary>
        public void PlayFile(string filePath)
        {
            if (!File.Exists(filePath)) return;

            if (!_videoViewReady)
            {
                _pendingFile = filePath;
                return;
            }

            _navigator.LoadDirectory(filePath);
            StartPlayingCurrentFile();
        }

        private void StartPlayingCurrentFile()
        {
            string? currentFile = _navigator.CurrentFile;
            if (currentFile == null || !File.Exists(currentFile) || _libVLC == null || _mediaPlayer == null)
                return;

            // Stejná opatrnost jako ve WPF verzi - Stop() před Play() nového
            // Media, ať rychlé opakované PageUp/PageDown nezpůsobí pád LibVLC.
            _mediaPlayer.Stop();

            using var media = new Media(_libVLC, new Uri(currentFile));
            _mediaPlayer.Play(media);
        }

        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_mediaPlayer == null) return;

            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;

                case Key.Space:
                    if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
                    else _mediaPlayer.Play();
                    e.Handled = true;
                    break;

                case Key.Up:
                    _mediaPlayer.Volume = Math.Min(100, _mediaPlayer.Volume + 5);
                    e.Handled = true;
                    break;

                case Key.Down:
                    _mediaPlayer.Volume = Math.Max(0, _mediaPlayer.Volume - 5);
                    e.Handled = true;
                    break;

                case Key.Right:
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(_mediaPlayer.Time + 5000));
                    e.Handled = true;
                    break;

                case Key.Left:
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time - 5000)));
                    e.Handled = true;
                    break;

                case Key.PageDown:
                    _navigator.GetNextFile();
                    StartPlayingCurrentFile();
                    e.Handled = true;
                    break;

                case Key.PageUp:
                    _navigator.GetPreviousFile();
                    StartPlayingCurrentFile();
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
            base.OnClosed(e);
        }
    }
}
