using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using PS150.Core;

namespace PS150.VideoEtAudio
{
    public partial class AudioPlayerWindow : Window
    {
        private readonly DirectoryNavigator _navigator = new();

        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private int _volume = 80;

        private readonly DispatcherTimer _timeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

        public AudioPlayerWindow()
        {
            InitializeComponent();

            // Stejná knihovna jako u videa - jen bez VideoView, čistě zvuk.
            // POZOR (Linux): libvlc musí být nainstalované přes apt
            // (sudo apt install vlc libvlc-dev), stejně jako u videa.
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC) { Volume = _volume };

            AddHandler(KeyDownEvent, AudioPlayerWindow_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            Opened += (s, e) => Focus();

            _timeRefreshTimer.Tick += (s, e) => UpdateTimeDisplay();
            _timeRefreshTimer.Start();

            Closed += (s, e) =>
            {
                _timeRefreshTimer.Stop();
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
                _libVLC?.Dispose();
            };
        }

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

            _mediaPlayer.Stop();

            using var media = new Media(_libVLC, new Uri(currentFile));
            _mediaPlayer.Play(media);

            FileNameText.Text = currentFile;
            UpdateTransportDisplay();
        }

        private void UpdateTimeDisplay()
        {
            if (_mediaPlayer == null) return;

            var current = TimeSpan.FromMilliseconds(Math.Max(_mediaPlayer.Time, 0));
            var total = TimeSpan.FromMilliseconds(Math.Max(_mediaPlayer.Length, 0));
            TimeText.Text = $"{current:mm\\:ss} / {total:mm\\:ss}";
            VolumeText.Text = $"Vol: {_volume}%";
        }

        private void UpdateTransportDisplay()
        {
            if (_mediaPlayer == null) return;
            TransportText.Text = _mediaPlayer.IsPlaying ? "[<<] [►] [>>]" : "[<<] [■] [>>]";
        }

        private void AudioPlayerWindow_KeyDown(object? sender, KeyEventArgs e)
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
                    UpdateTransportDisplay();
                    e.Handled = true;
                    break;

                case Key.Up:
                    _volume = Math.Min(100, _volume + 5);
                    _mediaPlayer.Volume = _volume;
                    UpdateTimeDisplay();
                    e.Handled = true;
                    break;

                case Key.Down:
                    _volume = Math.Max(0, _volume - 5);
                    _mediaPlayer.Volume = _volume;
                    UpdateTimeDisplay();
                    e.Handled = true;
                    break;

                case Key.Right:
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(_mediaPlayer.Time + 5000));
                    UpdateTimeDisplay();
                    e.Handled = true;
                    break;

                case Key.Left:
                    _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time - 5000)));
                    UpdateTimeDisplay();
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
    }
}
