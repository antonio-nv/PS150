using PS150.Core.Filters;
using System;

namespace PS150.Core.Generators
{
    /// <summary>
    /// Fyzikální model cembala (pracovní název "Randall &amp; Hopkirk" - žádný
    /// konkrétní historický nástroj, jen interní jméno presetu).
    ///
    /// Tělo tónu se stejně jako u PianoVoice simuluje metodou Karplus-Strong
    /// (viz KarplusStrongString.cs), tady ale jako JEDNA struna (bez unisonu -
    /// skutečné cembalo obvykle nemá dvě struny rozladěné do "živého" zázněru
    /// jako klavír) a s nižší hodnotou brightness => zpožďovací smyčka tlumí
    /// vyšší harmonické méně, takže zvuk zůstává jasnější a "kovovější", jak
    /// se na brnkanou strunu sluší.
    ///
    /// Dva hlavní jevy, které dělají cembalo cembalem (na rozdíl od klavíru):
    ///  1) BRNKNUTÍ BRČKEM (plectrum) - ostrý, velmi krátký šumový "click" na
    ///     začátku, kratší a "sušší" než úder klavírního kladívka (beze změny
    ///     oproti dřívějšku).
    ///  2) RYCHLEJŠÍ CELKOVÝ DOZVUK - struna cembala doznívá řádově rychleji
    ///     než klavírní (viz StringDecaySeconds v presetu).
    ///
    /// Dokud klávesu držíš, dozvuk řídí výhradně fyzikální model struny (KS
    /// smyčka) - ADSR obálka po náběhu zůstává na hladině 1.0 a neklesá.
    /// Teprve puštění klávesy spustí Release, jako přiblížení reálné dušičce
    /// (damperu), která strunu rychle utlumí.
    /// </summary>
    public class CembaloVoice : SynthVoice
    {
        private readonly KarplusStrongString _string;
        private readonly NoiseGenerator _exciteNoise = new NoiseGenerator();
        private bool _stringExcited = false;

        private readonly double _stringBrightness;
        private readonly double _stringDecaySeconds;
        private readonly double _pickPosition;
        private readonly double _excitationNoiseAmount;

        // --- Konec tónu podle prahu, ne (jen) podle pevného času ---
        private const double SilenceThresholdDb = -90.0;
        private static readonly double SilenceThresholdLinear = Math.Pow(10.0, SilenceThresholdDb / 20.0);
        private double _lastLevel = 1.0;

        // Pluck (brnknutí) - krátký ostrý šumový impuls (beze změny oproti dřívějšku)
        private double _pluckEnvelope = 1.0;
        private readonly NoiseGenerator _pluckNoise = new NoiseGenerator();
        private readonly BandPassFilter _pluckFilter = new BandPassFilter();
        private const double PluckDurationSec = 0.004;

        // Tělo struny i pluck jsou širokopásmový signál - musí se doopravdy
        // rozdělit skutečnou výhybkou mezi hloubky/středy/výšky.

        // Nepatrná rezonance ozvučné skříně - tenčí a "sušší" než u klavíru,
        // ale právě tahle přídavná barva dělá rozdíl mezi cembalem a holou
        // brnkanou strunou (harfou).
        private readonly BandPassFilter _bodyResonanceLow = new BandPassFilter();
        private readonly BandPassFilter _bodyResonanceHigh = new BandPassFilter();
        private const double BodyGainLow = 0.10;
        private const double BodyGainHigh = 0.14;

        public CembaloVoice(double sampleRate) : base(sampleRate)
        {
            _string = new KarplusStrongString(sampleRate);

            _stringBrightness = 0.30; // Nižší než klavír = jasnější, "kovovější" tón
            _stringDecaySeconds = 3.5; // Cembalo doznívá výrazně rychleji než klavír
            _pickPosition = 0.09;          // Blíž kraji = víc vyšších harmonických, ostřejší tón
            _excitationNoiseAmount = 0.10; // Brnknutí brčkem je "čistší" než úder plsti

            ConfigureEnvelope();

            _pluckFilter.SetParams(3800.0, 2.0, sampleRate);

            _bodyResonanceLow.SetParams(180.0, 3.5, sampleRate);
            _bodyResonanceHigh.SetParams(650.0, 2.5, sampleRate);
        }

        public CembaloVoice(VoicePreset preset, double sampleRate) : this(sampleRate)
        {
            if (preset != null)
            {
                _stringBrightness = Math.Clamp(preset.StringBrightness, 0.0, 0.98);
                _stringDecaySeconds = Math.Max(0.2, preset.StringDecaySeconds);
                _pickPosition = Math.Clamp(preset.PickPosition, 0.02, 0.5);
                _excitationNoiseAmount = Math.Clamp(preset.ExcitationNoiseAmount, 0.0, 1.0);

                double pluckFreq = preset.ChiffFilterFreqHz > 0 ? preset.ChiffFilterFreqHz : 3800.0;
                double pluckQ = preset.ChiffFilterQ > 0 ? preset.ChiffFilterQ : 2.0;
                _pluckFilter.SetParams(pluckFreq, pluckQ, sampleRate);
            }
        }

        private void ConfigureEnvelope()
        {
            NoteEnvelope.AttackTime = 0.001f;  // Prakticky okamžitý náběh
            NoteEnvelope.DecayTime = 0.01f;    // Rychle "usedne" na sustain
            NoteEnvelope.SustainLevel = 1.0f;  // ...a dál drží 1.0 - dozvuk řeší struna sama
            NoteEnvelope.ReleaseTime = 0.12f;  // Puštění klávesy = dušička (damper)
        }

        public override void NoteOn()
        {
            base.NoteOn();
            _pluckEnvelope = 1.0;
            _lastLevel = 1.0;
            _stringExcited = false; // "Znovu brnknout" - i kdyby předchozí tón ještě dozníval
        }

        public override bool IsFinished =>
            base.IsFinished || (HasStarted && _lastLevel < SilenceThresholdLinear);

        protected override double CalculateWaveform(double frequency)
        {
            // Strunu rozezníme až tady, při prvním vzorku po NoteOn() - teprve
            // tady známe skutečný kmitočet (viz komentář v ToneEngine.NoteOn).
            if (!_stringExcited)
            {
                _string.Excite(frequency, _stringBrightness, _stringDecaySeconds, _exciteNoise, (int)SampleRate, _pickPosition, _excitationNoiseAmount);
                _stringExcited = true;
            }

            double sum = 0.0;   // ZMĚNA: mono

            double stringSample = _string.NextSample();

            double body = _bodyResonanceLow.Process(stringSample) * BodyGainLow
                        + _bodyResonanceHigh.Process(stringSample) * BodyGainHigh;

            double toneSample = stringSample + body;

            sum += toneSample;

            _lastLevel = _lastLevel * 0.999 + Math.Abs(stringSample) * 0.001;

            // Pluck - ostrý krátký "click" na začátku (beze změny oproti dřívějšku).
            if (_pluckEnvelope > 0.001)
            {
                double noise = _pluckNoise.NextSample((int)SampleRate);
                double shapedPluck = _pluckFilter.Process(noise) * _pluckEnvelope * 0.25;

                sum += shapedPluck;

                _pluckEnvelope *= Math.Exp(-1.0 / (SampleRate * PluckDurationSec));
            }

            return sum;
        }
    }
}
