// Jednoduché textové přehrávání zvuku (mp3/wav/flac) přes LibVLC bez okna -
// běží přímo v terminálu, funguje i přes SSH. Zatím BEZ VU metrů a BEZ
// .mid (viz konverzace - MIDI banka/syntéza je samostatná neznámá,
// řešíme až po tomhle základu). Klávesy se čtou přes Console.ReadKey,
// stejný princip jako u VideoWindow, jen bez X11.

using System;
using System.IO;
using System.Threading;
using LibVLCSharp.Shared;
using PS150.Core;

namespace PS150.UI.Linux
{
    public sealed class AudioTextPlayer : IDisposable
    {
        private readonly DirectoryNavigator _navigator;
        private readonly LibVLC _libVLC;
        private readonly MediaPlayer _mediaPlayer;

        public string? HandoffFile { get; private set; }
        public bool QuitRequested { get; private set; }

        public AudioTextPlayer(DirectoryNavigator navigator)
        {
            _navigator = navigator;
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);
        }

        public void Run(string filePath)
        {
            string? currentFile = filePath;

            while (currentFile != null && HandoffFile == null && !QuitRequested)
            {
                currentFile = PlayOneFile(currentFile);
            }
        }

        private string? PlayOneFile(string filePath)
        {
            using var media = new Media(_libVLC, new Uri(Path.GetFullPath(filePath)));

            var endReached = new ManualResetEventSlim(false);
            void OnEndReached(object? s, EventArgs e) => endReached.Set();
            _mediaPlayer.EndReached += OnEndReached;

            try
            {
                _mediaPlayer.Media = media;
                _mediaPlayer.Play();

                Console.Clear();
                Console.WriteLine(Path.GetFullPath(filePath));
                Console.WriteLine();
                Console.WriteLine("mezerník = pauza/play,  ←/→ = -10 s / +10 s,  ↑/↓ = hlasitost");
                Console.WriteLine("PageUp/PageDown = předchozí/další soubor,  Esc/q = konec");
                Console.WriteLine();

                string? nextFile = null;
                bool running = true;

                while (running && !endReached.IsSet)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true).Key;

                        switch (key)
                        {
                            case ConsoleKey.Spacebar:
                                _mediaPlayer.SetPause(_mediaPlayer.IsPlaying);
                                break;

                            case ConsoleKey.RightArrow:
                            case ConsoleKey.LeftArrow:
                            {
                                long delta = key == ConsoleKey.RightArrow ? 10_000 : -10_000;
                                long length = _mediaPlayer.Length;
                                long target = Math.Max(0, _mediaPlayer.Time + delta);
                                if (length > 0) target = Math.Min(target, Math.Max(0, length - 500));
                                _mediaPlayer.Time = target;
                                break;
                            }

                            case ConsoleKey.UpArrow:
                            case ConsoleKey.DownArrow:
                            {
                                int volume = _mediaPlayer.Volume;
                                if (volume < 0) volume = 100;
                                _mediaPlayer.Volume = Math.Clamp(volume + (key == ConsoleKey.UpArrow ? 5 : -5), 0, 100);
                                break;
                            }

                            case ConsoleKey.PageDown:
                                nextFile = AdvanceOrHandoff(forward: true);
                                running = false;
                                break;

                            case ConsoleKey.PageUp:
                                nextFile = AdvanceOrHandoff(forward: false);
                                running = false;
                                break;

                            case ConsoleKey.Escape:
                            case ConsoleKey.Q:
                                QuitRequested = true;
                                running = false;
                                break;
                        }
                    }

                    PrintStatusLine();
                    Thread.Sleep(50);
                }

                if (endReached.IsSet && HandoffFile == null && !QuitRequested)
                {
                    nextFile = AdvanceOrHandoff(forward: true);
                }

                return nextFile;
            }
            finally
            {
                _mediaPlayer.EndReached -= OnEndReached;
                _mediaPlayer.Stop();
            }
        }

        private string? AdvanceOrHandoff(bool forward)
        {
            string? next = forward ? _navigator.GetNextFile() : _navigator.GetPreviousFile();
            if (next == null) { QuitRequested = true; return null; }

            if (MediaKind.IsVideo(next))
            {
                HandoffFile = next;
                return null;
            }

            return next;
        }

        private void PrintStatusLine()
        {
            long time = _mediaPlayer.Time;
            long length = _mediaPlayer.Length;
            string current = TimeSpan.FromMilliseconds(Math.Max(0, time)).ToString(@"mm\:ss");
            string total = length > 0 ? TimeSpan.FromMilliseconds(length).ToString(@"mm\:ss") : "--:--";
            string state = _mediaPlayer.IsPlaying ? "hraje" : "pauza";

            // \r přepíše stejný řádek - žádné blikání/scrollování konzole.
            Console.Write($"\r[{state}] {current} / {total}   ".PadRight(Console.WindowWidth - 1));
        }

        public void Dispose()
        {
            _mediaPlayer.Dispose();
            _libVLC.Dispose();
        }
    }
}
