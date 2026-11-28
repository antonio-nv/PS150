namespace PS150.Core.VUMeters
{
    /// <summary>
    /// krok72: neutrální (UI-agnostický) stav jedné buňky VU metru - stejný
    /// princip jako FieldStyle u FieldForm: Core řekne CO se má stát, konkrétní
    /// barvu (ConsoleColor, RGB...) si zvolí až UI vrstva.
    /// </summary>
    public enum MeterCellColor
    {
        Off,      // nesvítí - černá
        Normal,   // svítí, normální (tichý/střední) rozsah - zelená
        Hot       // svítí, limitace/nejsilnější hodnoty - červená
    }

    // krok84: přestěhováno z PS150.Core/AudioMeter.cs (jmenný prostor PS150.Core) sem,
    // do PS150.Core/05_VUmeters/ (jmenný prostor PS150.Core.VUMeters) - stejná logika
    // jako u 05_Filters/05_Equalizers/05_Mixers: číslo 05 pro celou signálovou/
    // metrovou rodinu. Zatím tu žije jen tahle společná matematika (mono i stereo
    // VU metr ve FileBrowserWindow.xaml.cs ji obě volají stejně - "mono"/"stereo"
    // je zatím jen otázka, kolikrát se zavolá a jak se to poskládá v UI, ne
    // samostatná Core třída), ale je tu místo, kdyby časem přibyly další varianty.
    public static class AudioMeter
    {
        /// <summary>
        /// Calculates logarithmic dB level from linear sample amplitude.
        /// Range: -120 dB to 0 dB.
        /// </summary>
        public static double LinearToDecibels(float peak)
        {
            if (peak <= 0.000001f) return -120.0; // Dynamic range floor

            double db = 20.0 * Math.Log10(peak);
            return Math.Max(-120.0, Math.Min(0.0, db));
        }

        /// <summary>
        /// krok72: mocninná (jedna hladká křivka, žádné lomení) stupnice VU metru -
        /// na rozdíl od dřívější lineární (db+120)/120 natáhne rozlišení těsně
        /// pod limitací (0dB) na úkor tichého konce, kde na přesnosti nezáleží.
        /// MinDb = -80dB (běžná praxe VU metrů, ne skutečná podlaha ticha -120dB
        /// z LinearToDecibels - vše tišší se slape úplně vlevo). Gamma > 1 =
        /// větší "natažení" nahoru k 0dB (2-3 je rozumný rozsah na vyzkoušení).
        /// </summary>
        private static double Warp(double db, double minDb, double maxDb, double gamma)
        {
            double normalized = Math.Clamp((db - minDb) / (maxDb - minDb), 0.0, 1.0);
            return Math.Pow(normalized, gamma);
        }

        /// <summary>
        /// krok72: vrátí barvu každé buňky pruhu VU metru (zleva doprava) podle
        /// aktuální hodnoty v dB - viz Warp výš. hotDb určuje, od které dB hodnoty
        /// (podle polohy na stupnici, ne podle počtu rozsvícených buněk) jsou
        /// rozsvícené buňky červené (limitace) místo zelené (normální rozsah).
        /// Nerozsvícené buňky jsou vždy Off/černé, bez ohledu na polohu.
        /// </summary>
        public static MeterCellColor[] RenderMeterCells(
            double db, int width, double minDb = -80.0, double maxDb = 0.0,
            double gamma = 2.0, double hotDb = -5.0)
        {
            int filled = (int)Math.Round(Warp(db, minDb, maxDb, gamma) * width);
            filled = Math.Clamp(filled, 0, width);

            int hotStart = (int)Math.Round(Warp(hotDb, minDb, maxDb, gamma) * width);
            hotStart = Math.Clamp(hotStart, 0, width);

            var cells = new MeterCellColor[width];
            for (int i = 0; i < width; i++)
            {
                cells[i] = i >= filled ? MeterCellColor.Off
                    : i >= hotStart ? MeterCellColor.Hot
                    : MeterCellColor.Normal;
            }
            return cells;
        }

        /// <summary>
        /// krok76: lineární výplň tahového potenciometru/faderu (ekvalizér ±12dB,
        /// kanálový fader mixpultu...) - na rozdíl od RenderMeterCells (mocninná
        /// křivka, tři barvy) jde o prostou lineární škálu se dvěma stavy: true =
        /// "ujetá dráha" od levého okraje (minValue) po aktuální hodnotu (UI barví
        /// šedě), false = zbytek dráhy (UI barví červeně - u faderu/potenciometru
        /// NENÍ nesvítící stav černý, pořád je to vidět jako "dráha", jen jinou
        /// barvou). minValue reprezentuje zobrazovanou dolní mez (např. -60dB
        /// místo skutečného -∞ v legendě).
        /// </summary>
        public static bool[] RenderFaderFill(double value, int width, double minValue, double maxValue)
        {
            double normalized = Math.Clamp((value - minValue) / (maxValue - minValue), 0.0, 1.0);
            int filled = Math.Clamp((int)Math.Round(normalized * width), 0, width);

            var cells = new bool[width];
            for (int i = 0; i < width; i++) cells[i] = i < filled;
            return cells;
        }
    }
}
