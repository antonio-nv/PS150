using PS150.Core.Filters;
using System;

namespace PS150.Core.Generators
{
    /// <summary>
    /// PROVIZORNÍ, ale POCTIVĚ POČÍTANÝ (ne vzorkovaný) zvuk brnkané/udeřené
    /// struny metodou ADITIVNÍ SYNTÉZY - součet několika sinusovek (partiálů)
    /// s trochou neharmoničnosti a KAŽDÝ partiál doznívá svou vlastní
    /// rychlostí (vyšší = rychleji). Žádný Karplus-Strong, žádné vzorky -
    /// jen sinusovky a exponenciální obálky, výpočetně triviální i pro
    /// Raspberry Pi.
    ///
    /// STEJNÝ PRINCIP JAKO BellVoice.cs, ale jako obecná "struna" - na rozdíl
    /// od zvonu tahle třída RESPEKTUJE puštění klávesy (dušička/damper), viz
    /// NoteOff().
    ///
    /// NEHARMONICITA: skutečná struna (na rozdíl od ideální) má partiály
    /// mírně VÝŠ, než by odpovídalo celočíselným násobkům - čím vyšší
    /// partiál, tím víc "utíká" nahoru. Používáme klasický vzorec pro tuhou
    /// strunu: f_n = n * f0 * sqrt(1 + B*n²), kde B (InharmonicityCoeff) je
    /// malé číslo (u klavíru typicky 0.0001-0.005, u cembala menší - tenčí
    /// nevinuté struny). Tohle jediné dodá tónu tu "živou", ne čistě
    /// syntetickou barvu, o kterou šlo od začátku.
    ///
    /// Všechny partiály se prostě sečtou do jednoho mono signálu. Rozdělení do
    /// pásem pro reproduktory řeší až výhybka na výstupu.
    /// </summary>
    public class AdditiveStringVoice : SynthVoice
    {
        private readonly double[] _phases;
        private double _elapsedSeconds = 0.0;

        private readonly double[] _partialRatiosIdeal;   // celočíselné 1,2,3,4...
        private readonly double[] _partialAmplitudes;
        private readonly double[] _partialDecayRates;
        private readonly double _inharmonicityCoeff;

        // Krátký šumový "úder/brnknutí" na začátku (kladívko/brčko) - stejný
        // princip jako hammer/pluck u PianoVoice/CembaloVoice, jen tady bez
        // vlastní třídy navíc, přímo v additivní struně.
        private double _transientEnvelope = 1.0;
        private readonly NoiseGenerator _transientNoise = new NoiseGenerator();
        private readonly BandPassFilter _transientFilter = new BandPassFilter();
        private readonly double _transientDurationSec;
        private readonly double _transientFreqHz;
        private readonly double _transientGain;

        private static readonly double[] DefaultPartialRatiosIdeal = { 1, 2, 3, 4, 5, 6, 7, 8 };
        private static readonly double[] DefaultPartialAmplitudes =
            { 0.50, 0.23, 0.14, 0.09, 0.05, 0.03, 0.013, 0.003 };
        private static readonly double[] DefaultPartialDecayRates =
            { 0.77, 1.34, 1.85, 2.33, 2.78, 3.22, 3.64, 4.05 };

        private const double SilenceThresholdDb = -90.0;
        private static readonly double SilenceThresholdLinear = Math.Pow(10.0, SilenceThresholdDb / 20.0);
        private double _lastPeakPartialLevel = 1.0;

        public AdditiveStringVoice(double sampleRate) : base(sampleRate)
        {
            _partialRatiosIdeal = DefaultPartialRatiosIdeal;
            _partialAmplitudes = DefaultPartialAmplitudes;
            _partialDecayRates = DefaultPartialDecayRates;
            _phases = new double[_partialRatiosIdeal.Length];
            _inharmonicityCoeff = 0.0003;

            _transientDurationSec = 0.006;
            _transientFreqHz = 500.0;
            _transientGain = 0.30;
            _transientFilter.SetParams(_transientFreqHz, 1.2, sampleRate);

            ConfigureEnvelope();
        }

        public AdditiveStringVoice(VoicePreset preset, double sampleRate) : base(sampleRate)
        {
            bool customPartials =
                preset?.PartialRatios != null &&
                preset.PartialAmplitudes != null &&
                preset.PartialDecayRates != null &&
                preset.PartialRatios.Length == preset.PartialAmplitudes.Length &&
                preset.PartialRatios.Length == preset.PartialDecayRates.Length;

            if (customPartials)
            {
                _partialRatiosIdeal = preset.PartialRatios;
                _partialAmplitudes = preset.PartialAmplitudes;
                _partialDecayRates = preset.PartialDecayRates;
            }
            else
            {
                _partialRatiosIdeal = DefaultPartialRatiosIdeal;
                _partialAmplitudes = DefaultPartialAmplitudes;
                _partialDecayRates = DefaultPartialDecayRates;
            }

            _phases = new double[_partialRatiosIdeal.Length];

            // Zneužíváme StringBrightness pole presetu jako "neharmonicitu" (B) -
            // obě pole u fyzikálního modelu i tady znamenají totéž: "jak moc
            // se struna chová jako ideální struna vs. jako tuhý drát".
            _inharmonicityCoeff = preset != null ? Math.Clamp(preset.StringBrightness, 0.0, 0.02) : 0.0003;

            _transientDurationSec = 0.006;
            _transientFreqHz = preset?.ChiffFilterFreqHz > 0 ? preset.ChiffFilterFreqHz : 500.0;
            double transientQ = preset?.ChiffFilterQ > 0 ? preset.ChiffFilterQ : 1.2;
            _transientGain = preset != null ? Math.Clamp(preset.ExcitationNoiseAmount, 0.0, 1.0) : 0.30;
            _transientFilter.SetParams(_transientFreqHz, transientQ, sampleRate);

            ConfigureEnvelope();
        }

        private void ConfigureEnvelope()
        {
            NoteEnvelope.AttackTime = 0.001f;
            NoteEnvelope.DecayTime = 0.01f;
            NoteEnvelope.SustainLevel = 1.0f;  // Dozvuk řeší partiály samy, ne obálka
            NoteEnvelope.ReleaseTime = 0.20f;  // Puštění klávesy = dušička (damper)
        }

        public override void NoteOn()
        {
            base.NoteOn();
            _elapsedSeconds = 0.0;
            _lastPeakPartialLevel = 1.0;
            _transientEnvelope = 1.0;
        }

        public override bool IsFinished =>
            base.IsFinished || (HasStarted && _lastPeakPartialLevel < SilenceThresholdLinear);

        protected override double CalculateWaveform(double frequency)
        {
            double sum = 0.0;   // ZMĚNA: mono
            double peakLevel = 0.0;

            for (int i = 0; i < _partialRatiosIdeal.Length; i++)
            {
                double n = _partialRatiosIdeal[i];

                // Neharmonicita tuhé struny - viz komentář u třídy.
                double stretchedRatio = n * Math.Sqrt(1.0 + _inharmonicityCoeff * n * n);

                double phase = AdvancePhase(ref _phases[i], stretchedRatio, frequency);
                double decay = Math.Exp(-_elapsedSeconds * _partialDecayRates[i]);
                double level = _partialAmplitudes[i] * decay;

                double value = Math.Sin(phase * 2.0 * Math.PI) * level;
                double partialFreq = frequency * stretchedRatio;
                sum += value;

                if (level > peakLevel) peakLevel = level;
            }

            _lastPeakPartialLevel = peakLevel;
            _elapsedSeconds += 1.0 / SampleRate;

            // Krátký šumový úder/brnknutí navrch (beze změny principu oproti
            // Piano/CembaloVoice) - úzkopásmový přes BandPassFilter, takže ho
            // stačí zařadit do JEDNOHO pásma podle jeho vlastní ladicí
            // frekvence (žádná plnohodnotná výhybka není potřeba ani tady).
            if (_transientEnvelope > 0.001)
            {
                double noise = _transientNoise.NextSample((int)SampleRate);
                double shaped = _transientFilter.Process(noise) * _transientEnvelope * _transientGain;
                sum += shaped;

                _transientEnvelope *= Math.Exp(-1.0 / (SampleRate * _transientDurationSec));
            }

            return sum;
        }
    }
}
