using PS150.Core.Generators;

namespace PS150.Core.Tones
{
    /// <summary>
    /// Rejstřík 700 - syntetizovaná bicí souprava GM (noty 35-81), viz
    /// DrumVoice.cs. Číslo MIDI noty vybírá KTERÝ zvuk zazní (kopák,
    /// virbl, hi-hat...), ne výšku tónu.
    ///
    /// Kmitočty/Q/šířky pásem níž jsou první odhad podle skutečných
    /// akustických rozsahů daných nástrojů (ne změřené/doladěné poslechem)
    /// - klidně to poslechem doupravuj, obzvlášť Q u NarrowBandpass, který
    /// zásadně ovlivňuje, jak dlouho a "tónově" daný buben dozvučí.
    /// </summary>
    public static class _700_Drums
    {
        public static readonly VoicePreset Preset = new VoicePreset
        {
            Number = 700,
            Name = "Drums GM",
            Instrument = InstrumentType.Drums,
            DrumParameters = new[]
            {
                // --- Bubny (rezonanční, tónové) ---
                new DrumParameter { GmNoteNumber = 35, Name = "Acoustic Bass Drum", Abbreviation = "AcBD", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 60,  Q = 2.5, Amplitude = 1.0 },
                new DrumParameter { GmNoteNumber = 36, Name = "Bass Drum 1",        Abbreviation = "BaDr", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 65,  Q = 2.5, Amplitude = 1.0 },
                new DrumParameter { GmNoteNumber = 41, Name = "Low Floor Tom",      Abbreviation = "LFTo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 90,  Q = 3.0, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 43, Name = "High Floor Tom",     Abbreviation = "HFTo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 110, Q = 3.0, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 45, Name = "Low Tom",            Abbreviation = "LoTo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 130, Q = 3.0, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 47, Name = "Low-Mid Tom",        Abbreviation = "LMTo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 155, Q = 3.0, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 48, Name = "Hi-Mid Tom",         Abbreviation = "HMTo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 180, Q = 3.0, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 50, Name = "High Tom",           Abbreviation = "HiTo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 210, Q = 3.0, Amplitude = 0.9 },

                // --- Virbl/tleskání (širokopásmové, praskavé) ---
                new DrumParameter { GmNoteNumber = 37, Name = "Side Stick",         Abbreviation = "SiSt", Method = DrumNoiseMethod.WideBandpass, LowHz = 800,  HighHz = 4000,  Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 38, Name = "Acoustic Snare",     Abbreviation = "AcSn", Method = DrumNoiseMethod.WideBandpass, LowHz = 180,  HighHz = 6000,  Amplitude = 1.0 },
                new DrumParameter { GmNoteNumber = 39, Name = "Hand Clap",          Abbreviation = "HaCl", Method = DrumNoiseMethod.WideBandpass, LowHz = 600,  HighHz = 3500,  Amplitude = 0.8 },
                new DrumParameter { GmNoteNumber = 40, Name = "Electric Snare",     Abbreviation = "ElSn", Method = DrumNoiseMethod.WideBandpass, LowHz = 200,  HighHz = 7000,  Amplitude = 1.0 },

                // --- Hi-hat/činely (širokopásmové, jasné/kovové) ---
                new DrumParameter { GmNoteNumber = 42, Name = "Closed Hi-Hat",      Abbreviation = "ClHH", Method = DrumNoiseMethod.WideBandpass, LowHz = 7000, HighHz = 15000, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 44, Name = "Pedal Hi-Hat",       Abbreviation = "PeHH", Method = DrumNoiseMethod.WideBandpass, LowHz = 6500, HighHz = 14000, Amplitude = 0.4 },
                new DrumParameter { GmNoteNumber = 46, Name = "Open Hi-Hat",        Abbreviation = "OpHH", Method = DrumNoiseMethod.WideBandpass, LowHz = 6000, HighHz = 15000, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 49, Name = "Crash Cymbal 1",     Abbreviation = "Cra1", Method = DrumNoiseMethod.WideBandpass, LowHz = 3000, HighHz = 15000, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 51, Name = "Ride Cymbal 1",      Abbreviation = "Rid1", Method = DrumNoiseMethod.WideBandpass, LowHz = 3500, HighHz = 12000, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 52, Name = "Chinese Cymbal",     Abbreviation = "ChCy", Method = DrumNoiseMethod.WideBandpass, LowHz = 2500, HighHz = 15000, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 53, Name = "Ride Bell",          Abbreviation = "RiBe", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 800, Q = 6.0, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 55, Name = "Splash Cymbal",      Abbreviation = "SpCy", Method = DrumNoiseMethod.WideBandpass, LowHz = 4000, HighHz = 15000, Amplitude = 0.8 },
                new DrumParameter { GmNoteNumber = 57, Name = "Crash Cymbal 2",     Abbreviation = "Cra2", Method = DrumNoiseMethod.WideBandpass, LowHz = 2800, HighHz = 15000, Amplitude = 0.9 },
                new DrumParameter { GmNoteNumber = 59, Name = "Ride Cymbal 2",      Abbreviation = "Rid2", Method = DrumNoiseMethod.WideBandpass, LowHz = 3500, HighHz = 12000, Amplitude = 0.6 },

                // --- Ostatní perkuse ---
                new DrumParameter { GmNoteNumber = 54, Name = "Tambourine",         Abbreviation = "Tamb", Method = DrumNoiseMethod.WideBandpass, LowHz = 4000, HighHz = 14000, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 56, Name = "Cowbell",            Abbreviation = "Cowb", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 560, Q = 8.0, Amplitude = 0.7 },
                new DrumParameter { GmNoteNumber = 58, Name = "Vibraslap",          Abbreviation = "Vibr", Method = DrumNoiseMethod.WideBandpass, LowHz = 500, HighHz = 5000, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 60, Name = "Hi Bongo",           Abbreviation = "HiBo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 280, Q = 4.0, Amplitude = 0.8 },
                new DrumParameter { GmNoteNumber = 61, Name = "Low Bongo",          Abbreviation = "LoBo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 200, Q = 4.0, Amplitude = 0.8 },
                new DrumParameter { GmNoteNumber = 62, Name = "Mute Hi Conga",      Abbreviation = "MHCo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 250, Q = 3.5, Amplitude = 0.7 },
                new DrumParameter { GmNoteNumber = 63, Name = "Open Hi Conga",      Abbreviation = "OHCo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 220, Q = 3.0, Amplitude = 0.8 },
                new DrumParameter { GmNoteNumber = 64, Name = "Low Conga",          Abbreviation = "LoCo", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 170, Q = 3.0, Amplitude = 0.8 },
                new DrumParameter { GmNoteNumber = 65, Name = "High Timbale",       Abbreviation = "HiTi", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 400, Q = 5.0, Amplitude = 0.7 },
                new DrumParameter { GmNoteNumber = 66, Name = "Low Timbale",        Abbreviation = "LoTi", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 300, Q = 5.0, Amplitude = 0.7 },
                new DrumParameter { GmNoteNumber = 67, Name = "High Agogo",         Abbreviation = "HiAg", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 900, Q = 8.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 68, Name = "Low Agogo",          Abbreviation = "LoAg", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 700, Q = 8.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 69, Name = "Cabasa",             Abbreviation = "Caba", Method = DrumNoiseMethod.WideBandpass, LowHz = 3000, HighHz = 12000, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 70, Name = "Maracas",            Abbreviation = "Mara", Method = DrumNoiseMethod.WideBandpass, LowHz = 3500, HighHz = 13000, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 71, Name = "Short Whistle",      Abbreviation = "ShWh", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 2200, Q = 12.0, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 72, Name = "Long Whistle",       Abbreviation = "LoWh", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 1800, Q = 10.0, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 73, Name = "Short Guiro",        Abbreviation = "ShGu", Method = DrumNoiseMethod.WideBandpass, LowHz = 2000, HighHz = 10000, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 74, Name = "Long Guiro",         Abbreviation = "LoGu", Method = DrumNoiseMethod.WideBandpass, LowHz = 2000, HighHz = 10000, Amplitude = 0.5 },
                new DrumParameter { GmNoteNumber = 75, Name = "Claves",             Abbreviation = "Clav", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 2500, Q = 15.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 76, Name = "Hi Wood Block",      Abbreviation = "HiWB", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 1400, Q = 10.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 77, Name = "Low Wood Block",     Abbreviation = "LoWB", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 1100, Q = 10.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 78, Name = "Mute Cuica",         Abbreviation = "MuCu", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 350, Q = 6.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 79, Name = "Open Cuica",         Abbreviation = "OpCu", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 450, Q = 5.0, Amplitude = 0.6 },
                new DrumParameter { GmNoteNumber = 80, Name = "Mute Triangle",      Abbreviation = "MuTr", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 3500, Q = 20.0, Amplitude = 0.4 },
                new DrumParameter { GmNoteNumber = 81, Name = "Open Triangle",      Abbreviation = "OpTr", Method = DrumNoiseMethod.NarrowBandpass, CenterHz = 4200, Q = 20.0, Amplitude = 0.5 },
            }
        };
    }
}
