using PS150.Core;            // Pro AudioEngine
using PS150.Core.Generators;
using PS150.Core.Input;      // Pro InputManager
using PS150.Core.Output;
using PS150.Core.ToneEngine; // TONEENGINE
using Melanchall.DryWetMidi.Core;         // krok58: NoteOnEvent/NoteOffEvent pro živé OPL3
using Melanchall.DryWetMidi.Common;       // krok58: SevenBitNumber/FourBitNumber
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
        private PS150.Core.Sampler.OplMidiSynth? _liveOpl;   // krok58: banka 8 pro živé hraní
        // krok102: _liveGm (banka 2, Windows GM) byla zrušena - viz komentář u PlayAsync v GmPianoMidiPlayer.cs.

        // Statická reference, aby k aktuálnímu AudioEngine (varhany) mohly
        // přistoupit i jiné statické třídy jako VgaEngine (pro VU metr).
        public static AudioEngine? OrganEngine { get; private set; }
        public static InputManager? Input { get; private set; }

        // krok58: stejná natvrdo odvozená cesta jako u přehrávače .mid (viz
        // GmPianoMidiPlayer.OplGenMidiPath) - vedle PS150.exe, OP2\GENMIDI.op2.
        private static readonly string OplGenMidiPath =
            System.IO.Path.Combine(AppContext.BaseDirectory, "OP2", "GENMIDI.op2");

        // krok63: "crash.log" vedle PS150.exe - viz InstallCrashLogger() níž.
        private static readonly string CrashLogPath =
            System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");

        /// <summary>
        /// krok63: globální zachytávání NEZACHYCENÝCH výjimek - na rozdíl od
        /// Visual Studia s připojeným debuggerem tohle funguje stejně
        /// v Release i Debug a nevyžaduje nic dalšího nastavovat. Hlavní
        /// důvod: pád appky při živém hraní (např. přepnutí rejstříku) nejspíš
        /// nastává na vlákně MIDI vstupu/zvukového callbacku, ne na UI vlákně
        /// - takové výjimky WPF samo o sobě nikam nezapíše, celá appka prostě
        /// zmizí. Tenhle zápis se stihne provést těsně PŘED tím, než běhové
        /// prostředí appku kvůli nezachycené výjimce ukončí (AppDomain.
        /// UnhandledException se volá synchronně, ještě než IsTerminating
        /// proběhne).
        /// </summary>
        private static void InstallCrashLogger()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                LogCrash("AppDomain.UnhandledException (nejspíš vlákno MIDI/zvuku)",
                    ex.ExceptionObject as Exception, ex.IsTerminating);

            // UI vlákno (kdyby pád přišel odsud) - necháváme ho dál spadnout
            // (ex.Handled zůstává false), jen navíc zalogujeme příčinu.
            Current.DispatcherUnhandledException += (s, ex) =>
                LogCrash("Application.DispatcherUnhandledException (UI vlákno)", ex.Exception, isTerminating: true);

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                LogCrash("TaskScheduler.UnobservedTaskException", ex.Exception, isTerminating: false);
                ex.SetObserved();
            };
        }

        private static void LogCrash(string source, Exception? ex, bool isTerminating)
        {
            try
            {
                string text =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} (terminating={isTerminating}){Environment.NewLine}" +
                    (ex?.ToString() ?? "(ExceptionObject není Exception - viz source výš)") +
                    Environment.NewLine + new string('-', 80) + Environment.NewLine;
                File.AppendAllText(CrashLogPath, text);
            }
            catch
            {
                // I samotné logování může selhat (např. nezapisovatelná složka) -
                // tady už nic nenaděláme, hlavně ať logování samo nezpůsobí další pád.
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // krok63: musí běžet jako úplně první věc - ať zachytí i cokoliv,
            // co by se pokazilo hned při startu.
            InstallCrashLogger();

            // NOVÉ: diagnostika SoundFontu - PS150.exe --sf2info "banka.sf2" zapíše přehled
            // vedle banky (banka.sf2.info.txt a banka.sf2.info_detail.txt) a skončí.
            if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--sf2info", StringComparison.OrdinalIgnoreCase))
            {
                string path = string.Join(" ", e.Args.Skip(1)).Trim('"');
                string report = PS150.Core.Sampler.Sf2Info.Write(path);
                System.Windows.MessageBox.Show("Hotovo: " + report, "PS150 SF2 info");
                Shutdown();
                return;
            }

            // krok20: diagnostika - PS150.exe --sf2render "banka.sf2" "vystup.wav" vykreslí zkušební tóny
            // klavíru a bicích do WAV a vedle zapíše vystup.wav.txt (zóny, smyčky, rychlost vykreslování).
            if (e.Args.Length >= 3 && string.Equals(e.Args[0], "--sf2render", StringComparison.OrdinalIgnoreCase))
            {
                // volitelný 4. parametr: cesta k .mid souboru, který se vykreslí místo zkušební scény
                string report = PS150.Core.Sampler.Sf2Render.Run(e.Args[1].Trim('"'), e.Args[2].Trim('"'),
                    e.Args.Length >= 4 ? e.Args[3].Trim('"') : null);
                System.Windows.MessageBox.Show("Hotovo: " + report, "PS150 SF2 render");
                Shutdown();
                return;
            }

            // --- KROK 0: UKONČENÍ PŘEDCHOZÍCH INSTANCÍ PROHLÍŽEČE ---
            KillPreviousInstances();

            base.OnStartup(e);

            // krok21: sběr paměti se snaží nedělat dlouhé pauzy (velké banky SF2 v paměti, zvuk nesmí přerušovat)
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            // 1. INICIALIZACE NOVÉHO TONEENGINE (Rejstříky / Varhany)
            _toneEngine = new ToneEngine();

            _audioEngine = new AudioEngine(_toneEngine);
            _audioEngine.Start();

            OrganEngine = _audioEngine;

            // krok58: banka 8 (OPL3/SoundBlaster) pro ŽIVÉ hraní - vlastní instance
            // OplMidiSynth BEZ vlastní zvukové karty (CreateForLiveMixing), AudioEngine
            // si ji sám "vyzvedává" v Read() a mixuje k varhanám (viz AttachLiveOpl).
            // Jména nástrojů pro formulář (Mel: Banka 008) jdou do stejného registru
            // jako SF2 banky (ToneEngine.RegisterOp2Bank) - skutečný zvuk ale odsud
            // nejde, ten hraje _liveOpl (viz AudioEngine.ToggleRegister).
            if (File.Exists(OplGenMidiPath))
            {
                try
                {
                    var op2Bank = PS150.Core.Sampler.Op2Bank.Load(OplGenMidiPath);
                    _liveOpl = PS150.Core.Sampler.OplMidiSynth.CreateForLiveMixing(op2Bank);
                    _audioEngine.AttachLiveOpl(_liveOpl);
                    int oplCount = ToneEngine.RegisterOp2Bank(PS150.Core.Banks._008_SoundBlaster.Number, op2Bank);
                    _audioEngine.RegisterExclusiveBank(
                        PS150.Core.Banks._008_SoundBlaster.Number,
                        program => _liveOpl?.SetUniversalProgram(program));
                    // krok66: tvrdé přepnutí banky (viz AudioEngine.HardSwitchMelodicBank) -
                    // setProgram(0) výš sám o sobě neumlčí už rozeznělé hlasy, jen zruší
                    // vynucený program pro příští notu.
                    _audioEngine.RegisterHardStop(
                        PS150.Core.Banks._008_SoundBlaster.Number,
                        () => _liveOpl?.StopAllNotesHard());
                    Debug.WriteLine($"[App] Živé OPL3: {oplCount} rejstříků z {OplGenMidiPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[App] Živé OPL3 se nepodařilo načíst: {ex.Message}");
                }
            }

            // krok102: dřívější živé GM (banka 2, Windows "Microsoft GS Wavetable
            // Synth" přes SharedGmOutput) bylo na výslovné přání uživatele ZRUŠENO -
            // viz podrobné zdůvodnění v GmPianoMidiPlayer.cs u PlayAsync. Veškerý
            // další vývoj (zpracování/syntéza/analýza) se teď soustředí jinam.

            // krok17/19: banky SF2 se načítají na pozadí (program mezitím hraje syntetické zvuky).
            // NATVRDO ze složky "sf2" vedle exe (nezávisle na settings.json): 003 MuseScore_General, 005 Bureå Church Ext.
            // Další banky lze dál přidat v settings.json (Sf2Banks); čísla 3 a 5 se tam ignorují.
            PS150.Core.Sampler.Sf2Loader.StartLoad(PS150.Core.Banks._003_MuseScore_General.Number, @"sf2\MuseScore_General.sf2");
            PS150.Core.Sampler.Sf2Loader.StartLoad(PS150.Core.Banks._005_Burea_Church_Ext.Number, @"sf2\Burea_Church_Ext_2.02.sf2");

            foreach (var bankSetting in AppSettings.Load().Sf2Banks)
            {
                if (bankSetting.Bank == PS150.Core.Banks._003_MuseScore_General.Number ||
                    bankSetting.Bank == PS150.Core.Banks._005_Burea_Church_Ext.Number) continue;

                if (!string.IsNullOrWhiteSpace(bankSetting.Path))
                    PS150.Core.Sampler.Sf2Loader.StartLoad(bankSetting.Bank, bankSetting.Path);
            }

            // 2. INICIALIZACE CORE INPUTU (Živé piano z USB / Casio)
            _inputManager = new InputManager();
            Input = _inputManager;

            _inputManager.OnInputEvent += evt =>
            {
                // ZMĚNA: kanál a dynamika jdou do syntezátoru (bicí na kanálu 10, velocity),
                // pedál sustain (CC64) platí pro klavírní rejstříky.
                if (evt.Type == InputEventType.ControlChange)
                {
                    if (evt.Controller == 64) _toneEngine?.SetSustain(evt.ControllerValue >= 64);
                    return;
                }

                bool isNoteOn = evt.Type == InputEventType.NoteOn && evt.Velocity > 0;

                if (isNoteOn)
                {
                    _toneEngine?.NoteOn(evt.Note.Number, evt.Channel, evt.Velocity);
                }
                else
                {
                    _toneEngine?.NoteOff(evt.Note.Number, evt.Channel);
                }

                // krok58: banka 8 (OPL3) hraje mimo ToneEngine (viz AudioEngine.ToggleRegister) -
                // dokud je z ní zapnutý nějaký rejstřík, melodické noty (ne bicí kanál) jdou
                // navíc i do živého OplMidiSynth. Kanál se posílá pořád stejný (0) - _liveOpl
                // zná jen jeden "živý" hlas, žádné MIDI vícekanálové směrování tu není potřeba.
                //
                // krok65: NoteOn se posílá, JEN KDYŽ je banka 8 právě aktivní (jako dřív) -
                // ale NoteOff se posílá VŽDYCKY (i když se mezitím banka přepnula pryč).
                // Jinak: podržíš klávesu na bance 8, přepneš na jinou banku, pustíš klávesu
                // - NoteOff se podle "je banka 8 aktivní?" zahodí, protože v tu chvíli už
                // není, a tón zůstane v OPL3 čipu navždy "zaseklý" (klávesa tam zůstává
                // logicky stisknutá). Poslat NoteOff navíc i tehdy, kdy daná nota na tomhle
                // enginu vůbec nehrála, je neškodné - prostě se ignoruje.
                if (_liveOpl != null && evt.Channel != ToneEngine.DrumChannel
                    && (!isNoteOn || (OrganEngine?.ActiveRegisters.Any(r => r.Bank == PS150.Core.Banks._008_SoundBlaster.Number) ?? false)))
                {
                    // krok58b: Channel je až na ChannelEvent (předek obou), ne na
                    // obecném MidiEvent - CS1061, kdyby proměnná byla deklarovaná jako MidiEvent.
                    ChannelEvent oplEvent = isNoteOn
                        ? new NoteOnEvent((SevenBitNumber)evt.Note.Number, (SevenBitNumber)Math.Clamp((int)Math.Round(evt.Velocity * 127), 1, 127))
                        : new NoteOffEvent((SevenBitNumber)evt.Note.Number, (SevenBitNumber)0);
                    oplEvent.Channel = (FourBitNumber)0;
                    _liveOpl.SendEvent(oplEvent);
                }

                // krok102: dřívější stejný blok pro banku 2 (Windows GM) byl zrušen -
                // viz komentář u OnStartup výš.

                Debug.WriteLine($"[{evt.Source}] {evt.Type} | Nota: {evt.Note.Number} ({evt.Note.FrequencyHz:F1} Hz)");
            };

            // ZMĚNA: otevírají se všechna MIDI-IN rozhraní, s hot-plugem (připojení/odpojení za běhu).
            _inputManager.StartMidiInputs();

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