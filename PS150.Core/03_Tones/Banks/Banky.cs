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

    /// <summary>Banka 1 - Syntetické zvuky (Melzerovy varhany Kutná Hora, klavíry, cembala, zvony, smyčce, bicí). Melzer 1-200 podle hracího stolu (fotky IMG_1744-1747), dále 200-202 klavíry, 300 cembalo, 400 zvon Zikmund, 500/501 aditivní, 600 smyčce, 700 bicí.</summary>
    public static class _001_SyntetickeZvuky
    {
        public const int Number = 1;
        public static readonly BankInfo Info = new BankInfo(Number, "Syntetické zvuky (Melzerovy varhany Kutná Hora, klavíry, cembala, zvony, smyčce, bicí)", BankKind.Synthetic, "Melzer 1-200 podle hracího stolu (fotky IMG_1744-1747), dále 200-202 klavíry, 300 cembalo, 400 zvon Zikmund, 500/501 aditivní, 600 smyčce, 700 bicí.");
    }

    /// <summary>Banka 2 - General MIDI (Microsoft GS Wavetable). Programy 1-128 podle GM; zatím hraje jen přehrávač .mid.</summary>
    public static class _002_GeneralMIDI
    {
        public const int Number = 2;
        public static readonly BankInfo Info = new BankInfo(Number, "General MIDI (Microsoft GS Wavetable)", BankKind.Planned, "Programy 1-128 podle GM; zatím hraje jen přehrávač .mid.");
    }

    /// <summary>Banka 3 - MuseScore_General (SF2). Bank/program podle B000-B128 z dokumentace banky; přehrávač SF2 bude až v dalším kroku.</summary>
    public static class _003_MuseScore_General
    {
        public const int Number = 3;
        public static readonly BankInfo Info = new BankInfo(Number, "MuseScore_General (SF2)", BankKind.Planned, "Bank/program podle B000-B128 z dokumentace banky; přehrávač SF2 bude až v dalším kroku.");
    }

    /// <summary>Banka 4 - Bureå Funeral Chapel 2.02 (SF2). Varhany z kaple ve Bureå (CC BY-SA), banka mimo repozitář.</summary>
    public static class _004_Burea_Funeral_Chapel
    {
        public const int Number = 4;
        public static readonly BankInfo Info = new BankInfo(Number, "Bureå Funeral Chapel 2.02 (SF2)", BankKind.Planned, "Varhany z kaple ve Bureå (CC BY-SA), banka mimo repozitář.");
    }

    /// <summary>Banka 5 - Bureå Church Ext 2.02 (SF2). Varhany z kostela ve Bureå, venkovní mikrofony, banka mimo repozitář.</summary>
    public static class _005_Burea_Church_Ext
    {
        public const int Number = 5;
        public static readonly BankInfo Info = new BankInfo(Number, "Bureå Church Ext 2.02 (SF2)", BankKind.Planned, "Varhany z kostela ve Bureå, venkovní mikrofony, banka mimo repozitář.");
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

    /// <summary>Banka 8 - SoundBlaster. Původní číslování staré banky SoundBlaster - doplní se.</summary>
    public static class _008_SoundBlaster
    {
        public const int Number = 8;
        public static readonly BankInfo Info = new BankInfo(Number, "SoundBlaster", BankKind.Planned, "Původní číslování staré banky SoundBlaster - doplní se.");
    }

    /// <summary>Seznam všech bank (nová banka = nová třída výš + řádek zde).</summary>
    public static class BankTable
    {
        public static readonly BankInfo[] All =
        {
            _001_SyntetickeZvuky.Info,
            _002_GeneralMIDI.Info,
            _003_MuseScore_General.Info,
            _004_Burea_Funeral_Chapel.Info,
            _005_Burea_Church_Ext.Info,
            _006_KORG.Info,
            _007_Amiga.Info,
            _008_SoundBlaster.Info,
        };

        public static BankInfo? Find(int number)
        {
            foreach (var b in All)
                if (b.Number == number) return b;
            return null;
        }
    }
}
