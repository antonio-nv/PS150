namespace PS150.Core.Banks
{
    /// <summary>Druh banky: Synthetic = hraje vestavěná syntéza, SoundFont = vzorky ze SF2, Planned = zatím jen rezervovaná.</summary>
    public enum BankKind { Synthetic, SoundFont, Planned }

    /// <summary>Popis jedné banky. Číslo a dlouhý (nezkrácený) název se zobrazují vždy spolu.</summary>
    public sealed class BankInfo
    {
        public int Number { get; }
        public string Name { get; }
        public BankKind Kind { get; }
        public string Description { get; }

        public BankInfo(int number, string name, BankKind kind, string description)
        {
            Number = number; Name = name; Kind = kind; Description = description;
        }

        /// <summary>Např. "001 Syntetické zvuky (...)".</summary>
        public string FullName => $"{Number:000} {Name}";
    }

    /// <summary>
    /// Banka 1 - Syntetické zvuky, TÓNY (Melzerovy varhany Kutná Hora,
    /// klavíry, cembala, zvony, smyčce). Melzer 1-200 podle hracího stolu
    /// (fotky IMG_1744-1747), dále 200-202 klavíry, 300 cembalo, 400 zvon
    /// Zikmund, 500/501 aditivní, 600 smyčce.
    /// krok40: bicí se odsud ODSTĚHOVALY do vlastní banky 9 (viz
    /// _009_SynteticBici níž) - nezávislé číslování, žádná kolize s
    /// melodickými rejstříky (dřív bicí = rejstřík 700 v týž bance, teď
    /// úplně jiná banka, takže klidně může mít číslo 1 jako tady Bombard16).
    /// </summary>
    public static class _001_SyntetickeZvuky
    {
        public const int Number = 1;
        // krok53: Name zkrácen na krátký kód (pro budoucí obrazovku živého
        // hraní, kde je málo místa) - plný popis zůstává v Description.
        public static readonly BankInfo Info = new BankInfo(Number, "Synth", BankKind.Synthetic, "Melzer 1-200 podle hracího stolu (fotky IMG_1744-1747), dále 200-202 klavíry, 300 cembalo, 400 zvon Zikmund, 500/501 aditivní, 600 smyčce. Bicí viz banka 9.");
    }

    // krok102: Banka 2 - General MIDI (Microsoft GS Wavetable) byla ZRUŠENA na
    // výslovné přání uživatele - celá cesta na Windows syntezátor (GsWavetableOut/
    // SharedGmOutput, .mid přehrávač i živé hraní) byla smazaná, viz komentář
    // u PlayAsync v GmPianoMidiPlayer.cs. Číslo 2 zůstává prázdné (nepoužité).

    /// <summary>Banka 3 - MuseScore_General (SF2). Bank/program podle B000-B128 z dokumentace banky; přehrávač SF2 bude až v dalším kroku.</summary>
    public static class _003_MuseScore_General
    {
        public const int Number = 3;
        // krok53: Name zkrácen na krátký kód.
        public static readonly BankInfo Info = new BankInfo(Number, "MuseScore", BankKind.Planned, "Bank/program podle B000-B128 z dokumentace banky; přehrávač SF2 bude až v dalším kroku.");
    }

    /// <summary>Banka 4 - Bureå Funeral Chapel 2.02 (SF2). Varhany z kaple ve Bureå (CC BY-SA), banka mimo repozitář.</summary>
    public static class _004_Burea_Funeral_Chapel
    {
        public const int Number = 4;
        // krok53: Name zkrácen na krátký kód.
        public static readonly BankInfo Info = new BankInfo(Number, "BureaFun", BankKind.Planned, "Varhany z kaple ve Bureå (CC BY-SA), banka mimo repozitář.");
    }

    /// <summary>Banka 5 - Bureå Church Ext 2.02 (SF2). Varhany z kostela ve Bureå, venkovní mikrofony, banka mimo repozitář.</summary>
    public static class _005_Burea_Church_Ext
    {
        public const int Number = 5;
        // krok53: Name zkrácen na krátký kód.
        public static readonly BankInfo Info = new BankInfo(Number, "BureaCh", BankKind.Planned, "Varhany z kostela ve Bureå, venkovní mikrofony, banka mimo repozitář.");
    }

    /// <summary>Banka 6 - KORG. Původní číslování KORGu - doplní se.</summary>
    public static class _006_KORG
    {
        public const int Number = 6;
        public static readonly BankInfo Info = new BankInfo(Number, "KORG", BankKind.Planned, "Původní číslování KORGu - doplní se.");
    }

    /// <summary>Banka 7 - Amiga. Původní číslování banky pro Amigu - doplní se.</summary>
    public static class _007_Amiga
    {
        public const int Number = 7;
        public static readonly BankInfo Info = new BankInfo(Number, "Amiga", BankKind.Planned, "Původní číslování banky pro Amigu - doplní se.");
    }

    /// <summary>krok35: Banka 8 - SoundBlaster (OPL3 emulace, GM přes .op2/GENMIDI banku - viz OplMidiSynth).</summary>
    public static class _008_SoundBlaster
    {
        public const int Number = 8;
        // krok53: Name zkrácen na krátký kód.
        public static readonly BankInfo Info = new BankInfo(Number, "OPL3", BankKind.SoundFont, "OPL3 emulace (NukedOpl) s GM instrumentální sadou z .op2/GENMIDI banky (DMXOPL apod.) - pro hrubou orientaci v prohlížeči souborů.");
    }

    /// <summary>
    /// krok40: Banka 9 - Syntetické zvuky, BICÍ (stejná syntéza jako banka 1,
    /// ale úplně NEZÁVISLÉ číslování rejstříků - viz PS150.Core/03_Percussion/
    /// _001_Drums.cs). Přesně jako u jednoúčelového bicího modulu: žádná
    /// melodická banka vedle sebe neexistuje, takže se číslo rejstříku může
    /// volit úplně svobodně od 1, bez ohledu na cokoliv v bance 1.
    /// </summary>
    public static class _009_SynteticBici
    {
        public const int Number = 9;
        // krok53: Name zkrácen na krátký kód.
        public static readonly BankInfo Info = new BankInfo(Number, "SynthBici", BankKind.Synthetic, "Vlastní, nezávislé číslování rejstříků bicích souprav - viz PS150.Core/03_Percussion. Dnes jen rejstřík 1 (GM souprava); časem klidně i další.");
    }

    /// <summary>Seznam všech bank (nová banka = nová třída výš + řádek zde).</summary>
    public static class BankTable
    {
        public static readonly BankInfo[] All =
        {
            _001_SyntetickeZvuky.Info,
            _003_MuseScore_General.Info,
            _004_Burea_Funeral_Chapel.Info,
            _005_Burea_Church_Ext.Info,
            _006_KORG.Info,
            _007_Amiga.Info,
            _008_SoundBlaster.Info,
            _009_SynteticBici.Info,
        };

        public static BankInfo? Find(int number)
        {
            foreach (var b in All)
                if (b.Number == number) return b;
            return null;
        }
    }
}
