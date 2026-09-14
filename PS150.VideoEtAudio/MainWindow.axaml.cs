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

        public MainWindow()
        {
            InitializeComponent();

            // POZOR (Linux): libvlc musí být nainstalované v systému přes apt
            // (sudo apt install vlc libvlc-dev) - stejně jako u PS150.UI.Linux.
            // Na Windows to obstará balíček VideoLAN.LibVLC.Windows.
            LibVLCSharp.Shared.Core.Initialize();

            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            var videoView = this.FindControl<VideoView>("VideoViewer");
            if (videoView != null)
            {
                videoView.MediaPlayer = _mediaPlayer;
            }

            KeyDown += MainWindow_KeyDown;
        }

        /// <summary>Volá se z App.axaml.cs, až je okno skutečně vytvořené (Opened) - ať je VideoView připravené dřív, než přijde Play().</summary>
        public void PlayFile(string filePath)
        {
            if (!File.Exists(filePath)) return;

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
