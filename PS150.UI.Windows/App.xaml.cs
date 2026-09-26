using PS150.Core;            // Pro AudioEngine
using PS150.Core.Generators;
using PS150.Core.Input;      // Pro InputManager
using PS150.Core.Output;
using PS150.Core.ToneEngine; // TONEENGINE
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace PS150.UI.Windows
{
    public static class MediaLauncher
    {
        public static void Launch(string filePath, InputManager inputManager)
        {
            // Smyčka střídající oba režimy podle toho, na jaký typ souboru
            // navigace (PageUp/PageDown, doehrání skladby) uvnitř VgaEngine
            // nebo MainWindow "přejede" - viz VgaEngine.Run() (vrací cestu
            // k videu, nebo null) a MainWindow.HandoffFile (totéž obráceně).
            // Dřív byly oba režimy úplně oddělené: přechod z videa na zvuk
            // (nebo naopak) se navenek buď vůbec nestal (audio prostě
            // začalo hrát potichu uvnitř fullscreen video okna), nebo appka
            // po ukončení jednoho režimu prostě skončila celá.
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string? nextFile = filePath;

            while (nextFile != null)
            {
                if (PS150.Core.MediaKind.IsVideo(nextFile))
                {
                    var window = new MainWindow();
                    Application.Current.MainWindow = window;

                    // PlayFile() se volá až v Loaded - ne hned po vytvoření
                    // okna - ať je VideoView (a tedy i nativní HWND, do
                    // kterého VLC kreslí obraz) opravdu připravené, než se
                    // pošle Play().
                    string fileToPlay = nextFile;
                    window.Loaded += (s, e) => window.PlayFile(fileToPlay);

                    // ShowDialog() blokuje, dokud se okno nezavře - stejné
                    // chování jako VgaEngine.Run() níž, takže se dá čekat
                    // jednotně na "co bylo dál".
                    window.ShowDialog();

                    nextFile = window.HandoffFile;
                }
                else
                {
                    // MIDI i ostatní zvukové soubory řeší FileBrowserWindow
                    // společně (viz GmPianoMidiPlayer pro .mid, AudioPlayer
                    // pro ostatní) - PS150.Core/ToneEngine zůstává nedotčený,
                    // slouží dál jen pro živé hraní z MIDI-IN klávesnice.
                    // Stejný ShowDialog()+Handoff vzor jako o pár řádků výš
                    // u MainWindow (video) - dřív tu bylo VgaEngine.Run(),
                    // což byla textová Windows konzole; teď normální WPF
                    // okno, viz FileBrowserWindow.xaml.cs.
                    var browserWindow = new FileBrowserWindow();
                    Application.Current.MainWindow = browserWindow;
                    browserWindow.LoadFile(nextFile);
                    browserWindow.ShowDialog();
                    nextFile = browserWindow.HandoffFile;
                }
            }

            Application.Current.Shutdown();
        }
    }

    public partial class App : Application
    {
        private InputManager? _inputManager;
        private ToneEngine? _toneEngine;
        private AudioEngine? _audioEngine;

        // Statická reference, aby k aktuálnímu AudioEngine (varhany) mohly
        // přistoupit i jiné statické třídy jako VgaEngine (pro VU metr).
        public static AudioEngine? OrganEngine { get; private set; }
        public static InputManager? Input { get; private set; }


        protected override void OnStartup(StartupEventArgs e)
        {
            // --- KROK 0: UKONČENÍ PŘEDCHOZÍCH INSTANCÍ PROHLÍŽEČE ---
            KillPreviousInstances();

            base.OnStartup(e);

            // 1. INICIALIZACE NOVÉHO TONEENGINE (Rejstříky / Varhany)
            _toneEngine = new ToneEngine();

            _audioEngine = new AudioEngine(_toneEngine);
            _audioEngine.Start();

            OrganEngine = _audioEngine;

            // 2. INICIALIZACE CORE INPUTU (Živé piano z USB / Casio)
            _inputManager = new InputManager();
            Input = _inputManager;

            _inputManager.OnInputEvent += evt =>
            {
                if (evt.Type == InputEventType.NoteOn && evt.Velocity > 0)
                {
                    _toneEngine?.NoteOn(evt.Note.Number);
                }
                else
                {
                    _toneEngine?.NoteOff(evt.Note.Number);
                }

                Debug.WriteLine($"[{evt.Source}] {evt.Type} | Nota: {evt.Note.Number} ({evt.Note.FrequencyHz:F1} Hz)");
            };

            _inputManager.StartLiveDevice("USB MIDI");

            // 3. NAČTENÍ NASTAVENÍ A KONTROLA ARGUMENTŮ
            AppSettings settings = AppSettings.Load();
            string? filePathToPlay = null;

            if (e.Args.Length == 0)
            {
                // Spuštěno bez parametrů (F5 / Debug / Start) -> načteme naposledy přehrávaný
                filePathToPlay = settings.LastFilePath;
            }
            else
            {
                // Spuštěno s parametrem -> načteme cestu ze souboru
                string rawPath = string.Join(" ", e.Args).Trim('"');
                try { filePathToPlay = Path.GetFullPath(rawPath); }
                catch { filePathToPlay = rawPath; }
            }

            // 4. SPUŠTĚNÍ PŘEHRÁVÁNÍ NEBO VGA KONZOLE
            if (!string.IsNullOrEmpty(filePathToPlay) && File.Exists(filePathToPlay))
            {
                settings.LastFilePath = filePathToPlay;
                settings.LastFolderPath = Path.GetDirectoryName(filePathToPlay);
                settings.Save();

                MediaLauncher.Launch(filePathToPlay, _inputManager);
            }
            else
            {
                // Pokud soubor neexistuje, otevře se okno naprázdno pro hraní na piano
                var browserWindow = new FileBrowserWindow();
                Application.Current.MainWindow = browserWindow;
                browserWindow.LoadFile("");
                browserWindow.ShowDialog();
                Shutdown();
            }
        }


        private void KillPreviousInstances()
        {
            try
            {
                Process currentProcess = Process.GetCurrentProcess();

                var previousProcesses = Process.GetProcessesByName(currentProcess.ProcessName)
                                               .Where(p => p.Id != currentProcess.Id);

                foreach (var process in previousProcesses)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    catch { /* ignorujeme chybové stavy pri zavírání */ }
                }
            }
            catch { /* ignorujeme chyby přístupu */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Úklid zdrojů při vypnutí
            _inputManager?.Dispose();
            _audioEngine?.Stop();
            _audioEngine?.Dispose();

            base.OnExit(e);
        }
    }
}