using System;
using System.IO;
using System.Text.Json;

namespace PS150.Core
{
    /// <summary>krok17: jedna banka SF2 - číslo banky (viz PS150.Core.Banks) a cesta k souboru.</summary>
    public class Sf2BankSetting
    {
        public int Bank { get; set; }
        public string Path { get; set; } = "";
    }

    public class AppSettings
    {
        public int Volume { get; set; } = 80;
        public bool DrumsEnabled { get; set; } = true;

        // NOVÉ: ladění / transpozice (klávesa t, viz PlayerTuning). Ve
        // starém settings.json tyhle položky chybí - použijí se výchozí
        // hodnoty níž (bez transpozice, A4 = 440 Hz).
        // krok29: zrušeno samostatné TuningActive - "vypnuto" teď prostě
        // znamená T=0 a A4=440 (viz PlayerTuning.IsNeutral). Starý
        // settings.json s klíčem "TuningActive" se dál normálně načte,
        // ten klíč se jen tiše ignoruje.
        public double TuningTransposeSemitones { get; set; } = 0.0;
        public double TuningReferenceA4Hz { get; set; } = 440.0;

        // NOVÉ: AJI (adaptivní čisté ladění .mid souborů, klávesa a) zapnuto/vypnuto.
        public bool AjiEnabled { get; set; } = false;

        // krok67: ladění/transpozice pro ŽIVÉ hraní (viz FileBrowserWindow.
        // ApplyLiveTuning/App.OrganEngine) - ÚMYSLNĚ nezávislé na
        // TuningTransposeSemitones/TuningReferenceA4Hz výš, to je jen pro
        // přehrávač .mid.
        public double LiveTransposeSemitones { get; set; } = 0.0;
        public double LiveReferenceA4Hz { get; set; } = 440.0;

        // krok69: AJI pro ŽIVÉ hraní (viz FileBrowserWindow/PS150.Core.ToneEngine.
        // LiveAjiSettings) - uloženo jako prostá čísla (ne enum), ať se starý
        // settings.json dál normálně načte, i kdyby se pořadí režimů/ladění
        // někdy přeskládalo.
        public int LiveAjiMode { get; set; } = 0;              // 0=Vyp, 1=Pevná, 2=Auto
        public int LiveAjiTemperament { get; set; } = 0;       // 0=JI-5, 1=Stredot., 2=Pythag.
        public int LiveAjiFixedTonicPc { get; set; } = 0;      // 0=C...11=H
        public bool LiveAjiFixedIsMinor { get; set; } = false;
        public double LiveAjiWindowSeconds { get; set; } = PS150.Core.AjiPlanner.WindowSeconds;
        public double LiveAjiHopSeconds { get; set; } = PS150.Core.AjiPlanner.HopSeconds;
        public double LiveAjiSwitchMargin { get; set; } = PS150.Core.AjiPlanner.SwitchMargin;
        public int LiveAjiMinSwitchHops { get; set; } = PS150.Core.AjiPlanner.MinSwitchHops;

        // krok28: banka (engine), kterou GmPianoMidiPlayer použije pro přehrávání
        // NEBICÍCH kanálů .mid souborů v prohlížeči - ÚMYSLNĚ nezávislé na
        // bankách živého hraní (TE.Banks/App.OrganEngine). Viz
        // GmPianoMidiPlayer.AvailablePlaybackBanks.
        public int MidiPlaybackBank { get; set; } = PS150.Core.Banks._003_MuseScore_General.Number;

        // krok30: totéž, ale jen pro kanál D10 (bicí) - nezávislé na
        // MidiPlaybackBank i na bankách živého hraní. Viz
        // GmPianoMidiPlayer.AvailableDrumBanks / DualEngineMidiOut.
        public int MidiDrumBank { get; set; } = PS150.Core.Banks._003_MuseScore_General.Number;

        // krok31: univerzální tónový rejstřík pro .mid soubory - "lidské" GM
        // číslování 1-128 (1 = Acoustic Grand Piano = staré natvrdo zapsané
        // chování). 0 = žádný univerzální rejstřík - hrají skutečné nástroje
        // podle Program Change v souboru. Viz GmPianoMidiPlayer.SetMidiMelodicRegister.
        public int MidiMelodicRegister { get; set; } = 1;

        // krok37: totéž, ale pro bicí sadu (kit) na kanálu D10 - "lidské" GM/GS
        // číslování 1-128 (1 = Standard kit P000 = staré natvrdo zapsané
        // chování). 0 = žádný vynucený rejstřík - hraje skutečná sada podle
        // Program Change na kanálu D10 v souboru. Viz GmPianoMidiPlayer.
        // SetMidiDrumRegister / Sf2MidiSynth.SetDrumProgram. U OPL banky
        // (SoundBlaster) se neprojeví - formát .op2 nemá víc bicích sad.
        public int MidiDrumRegister { get; set; } = 1;

        // NOVÉ: okno přehrávače otočené o 90 stupňů (klávesa r).
        public bool Rotated90 { get; set; } = false;

        // krok17: banky SF2 (mimo repozitář) a jejich čísla. Příklad ve settings.json:
        //   "Sf2Banks": [ { "Bank": 3, "Path": "C:\\banky\\MuseScore_General.sf2" } ]
        public System.Collections.Generic.List<Sf2BankSetting> Sf2Banks { get; set; } = new System.Collections.Generic.List<Sf2BankSetting>();

        public string? LastFolderPath { get; set; }
        public string? LastFilePath { get; set; }
        public long LastPositionMs { get; set; } = 0;

        // Poloha okna (levý horní roh, v souřadnicích celé virtuální
        // obrazovky - tj. napříč všemi monitory). Nullable, protože při
        // úplně prvním spuštění ještě nic uloženo není - viz VgaEngine.Run(),
        // kde se před obnovením ověřuje, že souřadnice pořád leží v rozsahu
        // aktuálně připojených monitorů (jinak by se okno mohlo otevřít
        // "mimo obrazovku", kdyby se od minula změnilo zapojení monitorů).
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }

        // NOVÉ: poloha okna ve svislém (otočeném) režimu - pamatuje se zvlášť
        // od vodorovného (WindowX/WindowY).
        public int? RotatedWindowX { get; set; }
        public int? RotatedWindowY { get; set; }

        private static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { /* V případě chyby vrátíme výchozí */ }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chyba při ukládání nastavení: {ex.Message}");
            }
        }

    }
}
