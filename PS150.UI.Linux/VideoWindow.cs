// Vlastní fullscreen X11 okno pro video (avi/mp4/mkv...) - založené na
// vyzkoušeném prototypu (PS150-LinuxFullscreenTest, test #2). LibVLC do
// okna jen kreslí (mediaPlayer.XWindow), klávesy zpracováváme sami a
// voláme přímo MediaPlayer API - stejný vzor jako MainWindow.xaml.cs na
// Windows.
//
// Jedna instance VideoWindow drží X11 okno + LibVLC OTEVŘENÉ mezi
// jednotlivými video soubory (PageUp/PageDown/EndReached mezi dvěma videi
// za sebou okno nezavírá a znovu neotvírá) - zavírá se, jen když navigace
// "přejede" na zvukový soubor (HandoffFile) nebo když uživatel ukončí
// appku úplně (Esc/q).

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;
using PS150.Core;

namespace PS150.UI.Linux
{
    internal static class X11
    {
        private const string Lib = "libX11.so.6";

        [DllImport(Lib)] public static extern int XInitThreads();
        [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr displayName);
        [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
        [DllImport(Lib)] public static extern int XDefaultScreen(IntPtr display);
        [DllImport(Lib)] public static extern IntPtr XRootWindow(IntPtr display, int screen);
        [DllImport(Lib)] public static extern int XDisplayWidth(IntPtr display, int screen);
        [DllImport(Lib)] public static extern int XDisplayHeight(IntPtr display, int screen);
        [DllImport(Lib)] public static extern ulong XBlackPixel(IntPtr display, int screen);
        [DllImport(Lib)] public static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint borderWidth, ulong border, ulong background);
        [DllImport(Lib)] public static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);
        [DllImport(Lib)] public static extern int XMapWindow(IntPtr display, IntPtr window);
        [DllImport(Lib)] public static extern int XDestroyWindow(IntPtr display, IntPtr window);
        [DllImport(Lib)] public static extern int XFlush(IntPtr display);
        [DllImport(Lib)] public static extern int XPending(IntPtr display);
        [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
        [DllImport(Lib)] public static extern ulong XLookupKeysym(IntPtr keyEvent, int index);
        [DllImport(Lib)] public static extern IntPtr XInternAtom(IntPtr display, string atomName, int onlyIfExists);
        [DllImport(Lib)] public static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr data, int nelements);
        [DllImport(Lib)] public static extern int XSetWMProtocols(IntPtr display, IntPtr window, IntPtr[] protocols, int count);
        [DllImport(Lib)] public static extern int XStoreName(IntPtr display, IntPtr window, string name);

        // --- Jen pro skrytí kurzoru (neviditelný 1x1 kurzor z prázdné pixmapy) ---
        [DllImport(Lib)] public static extern IntPtr XCreatePixmap(IntPtr display, IntPtr drawable, uint width, uint height, uint depth);
        [DllImport(Lib)] public static extern void XFreePixmap(IntPtr display, IntPtr pixmap);
        [DllImport(Lib)] public static extern IntPtr XCreatePixmapCursor(IntPtr display, IntPtr sourcePixmap, IntPtr maskPixmap, ref XColor fg, ref XColor bg, uint x, uint y);
        [DllImport(Lib)] public static extern int XDefineCursor(IntPtr display, IntPtr window, IntPtr cursor);
        [DllImport(Lib)] public static extern void XFreeCursor(IntPtr display, IntPtr cursor);

        [StructLayout(LayoutKind.Sequential)]
        public struct XColor
        {
            public ulong Pixel;
            public ushort Red, Green, Blue;
            public byte Flags;
            public byte Pad;
        }
    }

    public sealed class VideoWindow : IDisposable
    {
        private const int KeyPressEvent = 2;
        private const int ClientMessageEvent = 33;
        private const long KeyPressMask = 1L;

        private const ulong XK_space = 0x0020;
        private const ulong XK_q = 0x0071;
        private const ulong XK_Escape = 0xff1b;
        private const ulong XK_Left = 0xff51;
        private const ulong XK_Up = 0xff52;
        private const ulong XK_Right = 0xff53;
        private const ulong XK_Down = 0xff54;
        private const ulong XK_Prior = 0xff55; // PageUp
        private const ulong XK_Next = 0xff56;  // PageDown

        private readonly DirectoryNavigator _navigator;
        private readonly IntPtr _display;
        private readonly IntPtr _window;
        private readonly IntPtr _wmDelete;
        private readonly IntPtr _eventBuf;
        private readonly LibVLC _libVLC;
        private readonly MediaPlayer _mediaPlayer;

        /// <summary>Nastaveno, pokud navigace "přejela" na zvukový soubor - appka pak má přepnout na AudioTextPlayer.</summary>
        public string? HandoffFile { get; private set; }

        /// <summary>True, pokud uživatel appku ukončil úplně (Esc/q), ne jen přešel na jiný soubor.</summary>
        public bool QuitRequested { get; private set; }

        public VideoWindow(DirectoryNavigator navigator)
        {
            _navigator = navigator;

            // MUSÍ být úplně první volání Xlib v procesu.
            X11.XInitThreads();

            _display = X11.XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
                throw new InvalidOperationException("Nepodařilo se otevřít X display (DISPLAY nastavené? grafická session aktivní?).");

            int screen = X11.XDefaultScreen(_display);
            IntPtr root = X11.XRootWindow(_display, screen);
            uint width = (uint)X11.XDisplayWidth(_display, screen);
            uint height = (uint)X11.XDisplayHeight(_display, screen);
            ulong black = X11.XBlackPixel(_display, screen);

            _window = X11.XCreateSimpleWindow(_display, root, 0, 0, width, height, 0, black, black);
            X11.XStoreName(_display, _window, "PS150");
            X11.XSelectInput(_display, _window, KeyPressMask);

            IntPtr netWmState = X11.XInternAtom(_display, "_NET_WM_STATE", 0);
            IntPtr netWmFullscreen = X11.XInternAtom(_display, "_NET_WM_STATE_FULLSCREEN", 0);
            IntPtr xaAtom = (IntPtr)4; // XA_ATOM
            IntPtr propData = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(propData, netWmFullscreen);
            X11.XChangeProperty(_display, _window, netWmState, xaAtom, 32, 0, propData, 1);
            Marshal.FreeHGlobal(propData);

            _wmDelete = X11.XInternAtom(_display, "WM_DELETE_WINDOW", 0);
            X11.XSetWMProtocols(_display, _window, new[] { _wmDelete }, 1);

            HideCursor();

            X11.XMapWindow(_display, _window);
            X11.XFlush(_display);

            _eventBuf = Marshal.AllocHGlobal(256);

            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC)
            {
                EnableKeyInput = false,   // klávesy chceme MY, ne vestavěné libvlc "hotkeys" (viz test #1 - stejně nefungovaly)
                EnableMouseInput = false,
                XWindow = (uint)_window.ToInt64(),
            };
        }

        /// <summary>Přehraje soubory počínaje `filePath`, dokud navigace nepřejede na zvuk (HandoffFile) nebo uživatel neukončí appku (QuitRequested).</summary>
        public void Run(string filePath)
        {
            string? currentFile = filePath;

            while (currentFile != null && HandoffFile == null && !QuitRequested)
            {
                currentFile = PlayOneFile(currentFile);
            }
        }

        /// <returns>Cesta k dalšímu souboru, na který se má pokračovat (zůstává-li se ve videu), nebo null (konec/handoff/quit).</returns>
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

                ShowMarquee(Path.GetFileName(filePath));

                string? nextFile = null;
                bool running = true;

                while (running && !endReached.IsSet)
                {
                    while (X11.XPending(_display) > 0)
                    {
                        X11.XNextEvent(_display, _eventBuf);
                        int type = Marshal.ReadInt32(_eventBuf, 0);

                        if (type == KeyPressEvent)
                        {
                            ulong keysym = X11.XLookupKeysym(_eventBuf, 0);
                            var action = HandleKey(keysym);

                            if (action == KeyAction.Quit) { QuitRequested = true; running = false; }
                            else if (action == KeyAction.NextFile) { nextFile = AdvanceOrHandoff(forward: true); running = false; }
                            else if (action == KeyAction.PrevFile) { nextFile = AdvanceOrHandoff(forward: false); running = false; }
                        }
                        else if (type == ClientMessageEvent)
                        {
                            long data0 = Marshal.ReadInt64(_eventBuf, 56);
                            if (data0 == _wmDelete.ToInt64()) { QuitRequested = true; running = false; }
                        }
                    }

                    UpdateMarqueeTime();
                    Thread.Sleep(10);
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

        /// <summary>Posune DirectoryNavigator; pokud vyjde audio soubor, nastaví HandoffFile a vrátí null (PlayOneFile pak skončí). Pokud vyjde další video, vrátí ho k přehrání beze změny okna.</summary>
        private string? AdvanceOrHandoff(bool forward)
        {
            string? next = forward ? _navigator.GetNextFile() : _navigator.GetPreviousFile();
            if (next == null) { QuitRequested = true; return null; } // konec adresářů - nemáme kam pokračovat

            if (!MediaKind.IsVideo(next))
            {
                HandoffFile = next;
                return null;
            }

            return next;
        }

        private enum KeyAction { None, Quit, NextFile, PrevFile }

        private KeyAction HandleKey(ulong keysym)
        {
            switch (keysym)
            {
                case XK_space:
                    _mediaPlayer.SetPause(_mediaPlayer.IsPlaying);
                    return KeyAction.None;

                case XK_Right:
                case XK_Left:
                {
                    long delta = keysym == XK_Right ? 10_000 : -10_000;
                    long length = _mediaPlayer.Length;
                    long target = Math.Max(0, _mediaPlayer.Time + delta);
                    if (length > 0) target = Math.Min(target, Math.Max(0, length - 500));
                    _mediaPlayer.Time = target;
                    return KeyAction.None;
                }

                case XK_Up:
                case XK_Down:
                {
                    int volume = _mediaPlayer.Volume;
                    if (volume < 0) volume = 100;
                    _mediaPlayer.Volume = Math.Clamp(volume + (keysym == XK_Up ? 5 : -5), 0, 100);
                    return KeyAction.None;
                }

                case XK_Next: // PageDown
                    return KeyAction.NextFile;

                case XK_Prior: // PageUp
                    return KeyAction.PrevFile;

                case XK_Escape:
                case XK_q:
                    return KeyAction.Quit;

                default:
                    return KeyAction.None;
            }
        }

        // --- Textový překryv (jméno souboru, čas) přes libvlc "marquee" filtr ---
        // Nejistá část - v LibVLCSharp jsem tohle nezkoušel zkompilovat. Pokud
        // SetMarqueeInt/SetMarqueeString/VideoMarqueeOption neexistují přesně
        // pod těmihle jmény ve verzi, co se ti stáhne, klidně tuhle metodu (a
        // volání ShowMarquee/UpdateMarqueeTime níž) na první verzi smaž -
        // zbytek (okno, klávesy, navigace) na tom nezávisí.
        private void ShowMarquee(string fileName)
        {
            try
            {
                _mediaPlayer.SetMarqueeInt(VideoMarqueeOption.Enable, 1);
                _mediaPlayer.SetMarqueeString(VideoMarqueeOption.Text, fileName);
                _mediaPlayer.SetMarqueeInt(VideoMarqueeOption.Size, 24);
                _mediaPlayer.SetMarqueeInt(VideoMarqueeOption.Opacity, 200);
                _mediaPlayer.SetMarqueeInt(VideoMarqueeOption.Timeout, 0); // 0 = trvale, ne jen na pár vteřin
            }
            catch
            {
                // Marquee je jen kosmetický bonus - když selže, video ať klidně běží dál beze jména/času.
            }
        }

        private void UpdateMarqueeTime()
        {
            try
            {
                long time = _mediaPlayer.Time;
                long length = _mediaPlayer.Length;
                string current = TimeSpan.FromMilliseconds(Math.Max(0, time)).ToString(@"mm\:ss");
                string total = length > 0 ? TimeSpan.FromMilliseconds(length).ToString(@"mm\:ss") : "--:--";
                _mediaPlayer.SetMarqueeString(VideoMarqueeOption.Text, $"{Path.GetFileName(_mediaPlayer.Media?.Mrl ?? "")}  [{current}/{total}]");
            }
            catch
            {
                // Stejně jako výš - marquee je bonus, ne kritická funkce.
            }
        }

        private void HideCursor()
        {
            try
            {
                IntPtr blank = X11.XCreatePixmap(_display, _window, 1, 1, 1);
                var color = new X11.XColor();
                IntPtr cursor = X11.XCreatePixmapCursor(_display, blank, blank, ref color, ref color, 0, 0);
                X11.XDefineCursor(_display, _window, cursor);
                X11.XFreePixmap(_display, blank);
                // Kurzor samotný (ne pixmapy) necháváme žít, dokud žije okno -
                // XFreeCursor by ho zrušil dřív, než ho X server stihne použít.
            }
            catch
            {
                // Skrytí kurzoru je kosmetika (viz konverzace) - klidně beze změny, když tohle selže.
            }
        }

        public void Dispose()
        {
            _mediaPlayer.Dispose();
            _libVLC.Dispose();
            Marshal.FreeHGlobal(_eventBuf);
            X11.XDestroyWindow(_display, _window);
            X11.XFlush(_display);
            X11.XCloseDisplay(_display);
        }
    }
}
