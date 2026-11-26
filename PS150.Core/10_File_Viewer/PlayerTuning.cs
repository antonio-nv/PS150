using System;
using System.Globalization;

namespace PS150.Core
{
    /// <summary>
    /// Ladění přehrávače (klávesa t): transpozice v půltónech (i desetinná,
    /// +0,5 = čtvrttón nahoru = +50 centů) a referenční kmitočet A4 v Hz
    /// (440 = beze změny).
    ///
    /// krok29: ZRUŠEN samostatný přepínač "Lad zap/vyp" (dřív pole Active).
    /// Žádný efekt teď znamená prostě T=0 A A4=440 - nic navíc se nehlídá,
    /// "zapnuto/vypnuto" se odvozuje přímo z hodnot (viz TotalCents, IsNeutral).
    /// Klávesa T v okně dál dělá rychlé přepnutí tam a zpátky (viz
    /// FileBrowserWindow.ToggleTuning) - ale to je teď jen "pamatuj si poslední
    /// hodnoty a dej tam/pryč", ne vlastnost PlayerTuning samotného.
    ///
    /// Třída je NEMĚNNÁ (immutable) - po vytvoření se hodnoty nikdy nemění.
    /// Díky tomu si ji může UI vlákno i přehrávací vlákno předávat jako celek
    /// bez zámků: kdo drží odkaz, vidí vždycky konzistentní dvojici hodnot.
    /// Změna ladění = vytvoření nové instance.
    ///
    /// Jak se výsledek použije:
    ///  - zvukové soubory (mp3/wav/flac): změna rychlosti (varispeed),
    ///    viz SpeedFactor a AudioPlayer.SetSpeedFactor. Výška i tempo se mění
    ///    společně, jako u pásku, zato bez jakýchkoliv artefaktů.
    ///  - MIDI soubory: celé půltóny posunou číslo noty (WholeSemitones),
    ///    zbytek v centech (RemainderCents, vždy v rozsahu -50..+50) se přidá
    ///    k pitch bendu kanálu, viz GmPianoMidiPlayer.SetTuning.
    /// </summary>
    public sealed class PlayerTuning
    {
        public const double DefaultReferenceA4Hz = 440.0;
        public const double MaxTransposeSemitones = 12.0;
        public const double MinReferenceA4Hz = 400.0;
        public const double MaxReferenceA4Hz = 480.0;

        /// <summary>Ladění vypnuté, A4 = 440 Hz, bez transpozice.</summary>
        public static readonly PlayerTuning Off = new PlayerTuning(0.0, DefaultReferenceA4Hz);

        /// <summary>Transpozice v půltónech, +0,5 = čtvrttón nahoru. Rozsah ±12.</summary>
        public double TransposeSemitones { get; }

        /// <summary>Referenční kmitočet A4 v Hz. Rozsah 400-480, výchozí 440.</summary>
        public double ReferenceA4Hz { get; }

        public PlayerTuning(double transposeSemitones, double referenceA4Hz)
        {
            // NaN (např. ze zničeného settings.json) by prošlo přes Math.Clamp
            // beze změny a otrávilo by všechny další výpočty - proto vlastní ošetření.
            if (double.IsNaN(transposeSemitones) || double.IsInfinity(transposeSemitones)) transposeSemitones = 0.0;
            if (double.IsNaN(referenceA4Hz) || double.IsInfinity(referenceA4Hz)) referenceA4Hz = DefaultReferenceA4Hz;

            TransposeSemitones = Math.Clamp(transposeSemitones, -MaxTransposeSemitones, MaxTransposeSemitones);
            ReferenceA4Hz = Math.Clamp(referenceA4Hz, MinReferenceA4Hz, MaxReferenceA4Hz);
        }

        /// <summary>krok29: true, když T=0 a A4=440 - tedy žádný efekt (nahrazuje starý "Active").</summary>
        public bool IsNeutral => TransposeSemitones == 0.0 && ReferenceA4Hz == DefaultReferenceA4Hz;

        /// <summary>
        /// Celková odchylka v centech (1 půltón = 100 centů): transpozice plus
        /// posun daný referenčním A4. Při neutrálních hodnotách vyjde přesně 0.
        /// </summary>
        public double TotalCents =>
            TransposeSemitones * 100.0 + 1200.0 * Math.Log2(ReferenceA4Hz / DefaultReferenceA4Hz);

        /// <summary>
        /// Poměr rychlosti přehrávání pro varispeed: 2 na (centy / 1200).
        /// +100 centů (půltón nahoru) = 1,0595x rychleji. Bez ladění je to 1.
        /// </summary>
        public double SpeedFactor => Math.Pow(2.0, TotalCents / 1200.0);

        /// <summary>Celé půltóny (zaokrouhleno na nejbližší) - o tolik se posune číslo MIDI noty.</summary>
        public int WholeSemitones => (int)Math.Round(TotalCents / 100.0, MidpointRounding.AwayFromZero);

        /// <summary>Zbytek po odečtení celých půltónů, vždy -50..+50 centů - ten se dělá pitch bendem.</summary>
        public double RemainderCents => TotalCents - WholeSemitones * 100.0;

        /// <summary>
        /// Jednořádkový popis pro zobrazení, např. " T:+0.50 A:440.0 +50.0c".
        /// T = transpozice v půltónech, A = referenční A4 v Hz, na konci
        /// výsledná celková odchylka v centech. Vždy s tečkou (ne čárkou),
        /// ať nezáleží na jazyku systému.
        /// </summary>
        public string Describe()
        {
            string t = TransposeSemitones.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture);
            string a = ReferenceA4Hz.ToString("0.0", CultureInfo.InvariantCulture);
            string c = TotalCents.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture);
            return $" T:{t} A:{a} {c}c";
        }

        /// <summary>
        /// Zpracuje text zadaný uživatelem (klávesa INSERT):
        ///   "+0.5", "-2", "1,25"  -> transpozice v půltónech
        ///   "A442", "a 440.5"      -> referenční A4 v Hz
        /// Desetinná tečka i čárka jsou povolené.
        /// </summary>
        public static bool TryParseInput(string? text, PlayerTuning current, out PlayerTuning result, out string error)
        {
            result = current;
            error = "";

            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                error = "Prázdné zadání";
                return false;
            }

            bool isReference = text[0] == 'a' || text[0] == 'A';
            string numberText = (isReference ? text.Substring(1) : text).Trim().Replace(',', '.');

            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "Neplatné číslo";
                return false;
            }

            if (isReference)
            {
                if (value < MinReferenceA4Hz || value > MaxReferenceA4Hz)
                {
                    error = $"A: {MinReferenceA4Hz:0}-{MaxReferenceA4Hz:0} Hz";
                    return false;
                }
                result = new PlayerTuning(current.TransposeSemitones, value);
            }
            else
            {
                if (Math.Abs(value) > MaxTransposeSemitones)
                {
                    error = $"T: max +-{MaxTransposeSemitones:0} půltónů";
                    return false;
                }
                result = new PlayerTuning(value, current.ReferenceA4Hz);
            }

            return true;
        }
    }
}
