using System;

namespace PS150.Core.Generators
{
    /// <summary>
    /// Model velkého bronzového zvonu (inspirováno svatovítským Zikmundem, 1549) -
    /// jde o obecný fyzikální model zvonu, ne o změřený vzorek konkrétního zvonu.
    ///
    /// Reálný zvon nemá harmonické spektrum jako struna nebo píšťala - má tzv.
    /// PARTIÁLY s vlastními tradičními názvy a poměry vůči základní frekvenci:
    ///   Hum     (~0.5x)  - hluboký "podtón", nejdéle doznívající
    ///   Prime   (1.0x)   - základní tón
    ///   Tierce  (~1.2x)  - malá tercie (dává zvonu ten charakteristický "mollový" nádech)
    ///   Kvinta  (~1.5x)
    ///   Nominál (2.0x)   - nejsilnější partiál, ten, který ucho vnímá jako "výšku" zvonu
    ///
    /// DŮLEŽITÁ ZMĚNA: sdílená ADSR obálka (NoteEnvelope) už neurčuje doznívání -
    /// po rychlém náběhu zůstává na hladině 1.0 a NEKLESÁ. O skutečné doznívání se
    /// stará VÝHRADNĚ tenhle fyzikální model (PartialDecayRates). Konec tónu určuje
    /// pokles skutečné hladiny signálu pod práh SilenceThresholdDb (-90 dB), ne
    /// pevně daný čas. Zvon navíc ignoruje NoteOff (viz override níže) - jednou
    /// udeřený zvon dozní sám, bez ohledu na to, jestli klávesu ještě držíš.
    ///
    /// Navíc: typické "vlnění/třepotání" (beating) velkých zvonů vzniká, když zvon
    /// není dokonale osově symetrický - dvě velmi blízké frekvence (např. dva mírně
    /// rozladěné Nominály) spolu interferují a hlasitost pravidelně "pulzuje".
    /// </summary>
    public class BellVoice : SynthVoice
    {
        private readonly double[] _phases = new double[7];
        private double _elapsedSeconds = 0.0;

        // Poměry, amplitudy a rychlosti dozvuku partiálů - buď z presetu, nebo výchozí.
        private readonly double[] _partialRatios;
        private readonly double[] _partialAmplitudes;
        private readonly double[] _partialDecayRates;

        // Výchozí obecný model zvonu (Hum, Prime, Tierce, Kvinta, Nominál, +2 vyšší)
        private static readonly double[] DefaultPartialRatios =
        {
            0.501, 1.000, 1.199, 1.502, 2.000, 2.514, 3.011
        };
        private static readonly double[] DefaultPartialAmplitudes =
        {
            0.35, 0.55, 0.40, 0.30, 0.50, 0.20, 0.12
        };
        private static readonly double[] DefaultPartialDecayRates =
        {
            0.12, 0.35, 0.55, 0.70, 0.45, 1.10, 1.60
        };

        private const int NominalIndex = 4;

        // Druhý, mírně rozladěný Nominál pro efekt "vlnění" (beating)
        private const double DetunedNominalRatio = 2.006;
        private double _detunedNominalPhase = 0.0;

        // --- Konec tónu podle prahu, ne podle pevného času ---
        // -90 dB = běžně používaný práh "prakticky ticho" (linearně cca 0,0000316).
        private const double SilenceThresholdDb = -90.0;
        private static readonly double SilenceThresholdLinear = Math.Pow(10.0, SilenceThresholdDb / 20.0);

        // Nejhlasitější aktuálně dozvučující partiál - aktualizuje se každý vzorek
        // v CalculateWaveform, čte se v IsFinished.
        private double _lastPeakPartialLevel = 1.0;

        // --- FM "wobble" (nakřáplost) - volitelné, řízené z VoicePreset ---
        private double _modPhase = 0.0;
        private readonly double _modSpeedHz;
        private readonly double _modDepth;
        private readonly bool _modEnabled;

        public BellVoice(double sampleRate) : base(sampleRate)
        {
            _partialRatios = DefaultPartialRatios;
            _partialAmplitudes = DefaultPartialAmplitudes;
            _partialDecayRates = DefaultPartialDecayRates;
            _phases = new double[_partialRatios.Length];

            ConfigureEnvelope();

            _modEnabled = false;
        }

        public BellVoice(VoicePreset preset, double sampleRate) : base(sampleRate)
        {
            bool customPartials =
                preset?.PartialRatios != null &&
                preset.PartialAmplitudes != null &&
                preset.PartialDecayRates != null &&
                preset.PartialRatios.Length == preset.PartialAmplitudes.Length &&
                preset.PartialRatios.Length == preset.PartialDecayRates.Length;

            if (customPartials)
            {
                _partialRatios = preset.PartialRatios;
                _partialAmplitudes = preset.PartialAmplitudes;
                _partialDecayRates = preset.PartialDecayRates;
            }
            else
            {
                if (preset?.PartialRatios != null || preset?.PartialAmplitudes != null || preset?.PartialDecayRates != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BellVoice] Preset '{preset?.Name}' má nekompletní/nesouhlasící partiály - použit výchozí model zvonu.");
                }

                _partialRatios = DefaultPartialRatios;
                _partialAmplitudes = DefaultPartialAmplitudes;
                _partialDecayRates = DefaultPartialDecayRates;
            }

            _phases = new double[_partialRatios.Length];

            ConfigureEnvelope();

            if (preset != null && preset.ModType == ModulationType.FM)
            {
                _modEnabled = true;
                _modSpeedHz = preset.ModSpeedHz;
                _modDepth = preset.ModDepth;
            }
        }

        private void ConfigureEnvelope()
        {
            // Obálka teď slouží JEN k rychlému náběhu (úder srdce o plášť).
            // SustainLevel = 1.0 a minimální DecayTime znamená, že po náběhu
            // zůstane na hladině 1.0 a dál neklesá - o doznívání se stará
            // výhradně fyzikální model partiálů (PartialDecayRates).
            NoteEnvelope.AttackTime = 0.002f;
            NoteEnvelope.DecayTime = 0.01f;
            NoteEnvelope.SustainLevel = 1.0f;
            NoteEnvelope.ReleaseTime = 2.5f; // fakticky nevyužito, viz NoteOff() níže
        }

        public override void NoteOn()
        {
            base.NoteOn();
            _elapsedSeconds = 0.0;
            _lastPeakPartialLevel = 1.0;
        }

        // Zvon ignoruje puštění klávesy - jednou udeřený zvon dozní sám podle
        // vlastní fyziky, bez ohledu na to, jak dlouho klávesu držíš. Skutečné
        // ukončení hlasu řeší IsFinished (práh -90 dB) níže, ne Release.
        public override void NoteOff()
        {
            // Záměrně prázdné.
        }

        public override bool IsFinished =>
            base.IsFinished || (HasStarted && _lastPeakPartialLevel < SilenceThresholdLinear);

        protected override double CalculateWaveform(double frequency)
        {
            double effectiveFrequency = frequency;

            if (_modEnabled)
            {
                double modPhase = AdvancePhase(ref _modPhase, 1.0, _modSpeedHz);
                double modulator = Math.Sin(modPhase * 2.0 * Math.PI);
                effectiveFrequency = frequency * (1.0 + modulator * _modDepth);
            }

            double sum = 0.0;   // ZMĚNA: mono
            double peakLevel = 0.0;

            for (int i = 0; i < _partialRatios.Length; i++)
            {
                double phase = AdvancePhase(ref _phases[i], _partialRatios[i], effectiveFrequency);
                double decay = Math.Exp(-_elapsedSeconds * _partialDecayRates[i]);
                double level = _partialAmplitudes[i] * decay;

                double value = Math.Sin(phase * 2.0 * Math.PI) * level;
                double partialFreq = effectiveFrequency * _partialRatios[i];
                sum += value;

                if (level > peakLevel) peakLevel = level;
            }

            if (_partialRatios.Length > NominalIndex)
            {
                double detunedPhase = AdvancePhase(ref _detunedNominalPhase, DetunedNominalRatio, effectiveFrequency);
                double nominalDecay = Math.Exp(-_elapsedSeconds * _partialDecayRates[NominalIndex]);
                double detunedLevel = _partialAmplitudes[NominalIndex] * nominalDecay;

                double detunedValue = Math.Sin(detunedPhase * 2.0 * Math.PI) * detunedLevel;
                double detunedFreq = effectiveFrequency * DetunedNominalRatio;
                sum += detunedValue;

                if (detunedLevel > peakLevel) peakLevel = detunedLevel;
            }

            _lastPeakPartialLevel = peakLevel;
            _elapsedSeconds += 1.0 / SampleRate;

            // Normalizace - partiálů je dohromady 8, ať zvon nepřebuzuje výstup
            return sum * 0.3;
        }
    }
}
