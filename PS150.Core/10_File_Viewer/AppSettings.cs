using System;
using System.IO;
using System.Text.Json;

namespace PS150.Core
{
    public class AppSettings
    {
        public int Volume { get; set; } = 80;
        public bool DrumsEnabled { get; set; } = true;

        // NOVÉ: ladění / transpozice (klávesa t, viz PlayerTuning). Ve
        // starém settings.json tyhle položky chybí - použijí se výchozí
        // hodnoty níž (ladění vypnuté, A4 = 440 Hz, bez transpozice).
        public bool TuningActive { get; set; } = false;
        public double TuningTransposeSemitones { get; set; } = 0.0;
        public double TuningReferenceA4Hz { get; set; } = 440.0;

        // NOVÉ: AJI (adaptivní čisté ladění .mid souborů, klávesa a) zapnuto/vypnuto.
        public bool AjiEnabled { get; set; } = false;

        // NOVÉ: okno přehrávače otočené o 90 stupňů (klávesa r).
        public bool Rotated90 { get; set; } = false;

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