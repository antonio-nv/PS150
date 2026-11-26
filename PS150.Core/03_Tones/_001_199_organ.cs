using PS150.Core.Generators;

namespace PS150.Core.Tones
{
    public static class _001_Bombard16     {
        public static VoicePreset Preset => new VoicePreset
        {
            // TADY JE TA ZMĚNA: Přidat přesný typ pole
            Name = "Bombard16Preset",
            Number = 01,
            PartialRatios = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 },
            PartialAmplitudes = new[] { 1.0, 0.75, 0.60, 0.40, 0.25 },
            ChiffFilterFreqHz = 800.0,
            ChiffFilterQ = 1.2,
            ModType = ModulationType.AM,
            ModSpeedHz = 5.5,
            ModDepth = 0.08
        };
    }

    public static class _002_ViolnBas16 { 
            public static VoicePreset Preset => new VoicePreset
        {
            // TADY JE TA ZMĚNA: Přidat přesný typ pole
            Name = "ViolnBas16",
            Number = 02,
            PartialRatios = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 },
            PartialAmplitudes = new[] { 1.0, 0.75, 0.60, 0.40, 0.25 },
            ChiffFilterFreqHz = 800.0,
            ChiffFilterQ = 1.2,
            ModType = ModulationType.AM,
            ModSpeedHz = 5.5,
            ModDepth = 0.08
        };
    }

    // krok23: ukázkový rejstřík s AmplitudeEnvelope (viz VoicePreset.cs) -
    // síla alikvot tří uzlů (nejhlubší/prostřední/nejvyšší hraná nota),
    // mezi nimi OrganVoice lineárně interpoluje. Čísla jsou ZATÍM ILUSTRAČNÍ,
    // nahradit po reálné analýze přes PS150.Analyzer.
    public static class _108_VoxCelesta
    {
        private static readonly double[] Harmonics = { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 };

        private static readonly PartialBreakpoint[] Envelope =
        {
            //                 1.00  2.00  3.00  4.00  5.00  6.00
            new(Note: 36, new[] { 1.00, 0.55, 0.20, 0.08, 0.03, 0.01 }),   // nejhlubší - C2
            new(Note: 60, new[] { 1.00, 0.40, 0.14, 0.05, 0.02, 0.00 }),   // prostřední - C4
            new(Note: 84, new[] { 1.00, 0.25, 0.07, 0.02, 0.00, 0.00 }),   // nejvyšší - C6
        };

        public static VoicePreset Preset => new VoicePreset
        {
            Name = "VoxCelesta",
            Number = 108,
            PartialRatios = Harmonics,
            PartialAmplitudes = Envelope[1].Amplitudes, // uprostřed - fallback, kdyby Envelope někdo vynuloval
            AmplitudeEnvelope = Envelope,
            ChiffFilterFreqHz = 2200.0,
            ChiffFilterQ = 0.9,
            ModType = ModulationType.None,
            ModSpeedHz = 0.0,
            ModDepth = 0.0
        };
    }

    public static class _085_Aeolus
    {
        // Stejně jako u Cembalo presetu - zvuk je "zadrátovaný" v BellVoice,
        // preset zatím jen vybírá zvukový engine.
        public static VoicePreset Preset => new VoicePreset
        {
            Name = "Aeolus",
            Number = 85,
            Instrument = InstrumentType.Bell,

            // Vlastní partiály - klidně i jiný počet než výchozích 7
            PartialRatios = new[] { 0.50, 1.00, 1.19, 1.55, 2.02, 2.60 },
            PartialAmplitudes = new[] { 0.40, 0.60, 0.35, 0.30, 0.55, 0.15 },
            PartialDecayRates = new[] { 0.15, 0.30, 0.60, 0.75, 0.40, 1.20 },

            ChiffFilterFreqHz = 800.0,
            ChiffFilterQ = 1.2,
            ModType = ModulationType.FM,
            ModSpeedHz = 5.5,
            ModDepth = 0.08
        };
    }

   





}