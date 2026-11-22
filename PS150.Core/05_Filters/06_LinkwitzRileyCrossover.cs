using System;

namespace PS150.Core.Filters
{
    /// <summary>
    /// Dvoupásmová aktivní výhybka Linkwitz-Riley 4. řádu (24 dB/okt) pro JEDEN kanál:
    /// každá větev jsou dva Butterworthovy biquady za sebou. Výhody:
    ///  - v dělicím kmitočtu mají obě větve -6 dB a jsou ve fázi,
    ///  - SOUČET hloubek a výšek má rovný kmitočtový průběh (mění se jen fáze),
    ///  - strmost 24 dB/okt dobře chrání výškový reproduktor před hlubokými kmitočty.
    ///
    /// Každý kanál (levý, pravý) musí mít svou vlastní instanci, drží si stav filtrů.
    /// Pro stereo viz StereoCrossover2Way.
    /// </summary>
    public sealed class LinkwitzRileyCrossover
    {
        private readonly Biquad _lowA = new Biquad();
        private readonly Biquad _lowB = new Biquad();
        private readonly Biquad _highA = new Biquad();
        private readonly Biquad _highB = new Biquad();

        public double SampleRate { get; }
        public double CrossoverHz { get; private set; }

        public LinkwitzRileyCrossover(double sampleRate, double crossoverHz)
        {
            SampleRate = sampleRate;
            SetCrossover(crossoverHz);
        }

        /// <summary>
        /// Změní dělicí kmitočet (i za chodu - stav filtrů se zachová, takže to nelupne).
        /// Zatím se volá jen při vytvoření, ale návrh už s měnitelným kmitočtem počítá.
        /// </summary>
        public void SetCrossover(double crossoverHz)
        {
            _lowA.SetLowPass(SampleRate, crossoverHz);
            _lowB.SetLowPass(SampleRate, crossoverHz);
            _highA.SetHighPass(SampleRate, crossoverHz);
            _highB.SetHighPass(SampleRate, crossoverHz);
            CrossoverHz = crossoverHz;
        }

        /// <summary>Rozdělí jeden vzorek na hloubky a výšky.</summary>
        public void Process(double input, out double low, out double high)
        {
            low = _lowB.Process(_lowA.Process(input));
            high = _highB.Process(_highA.Process(input));
        }

        public void Reset()
        {
            _lowA.Reset();
            _lowB.Reset();
            _highA.Reset();
            _highB.Reset();
        }
    }

    /// <summary>
    /// Stereo dvoupásmová výhybka: levý a pravý kanál zvlášť, stejný dělicí kmitočet.
    /// </summary>
    public sealed class StereoCrossover2Way
    {
        private readonly LinkwitzRileyCrossover _left;
        private readonly LinkwitzRileyCrossover _right;

        public double CrossoverHz => _left.CrossoverHz;

        public StereoCrossover2Way(double sampleRate, double crossoverHz)
        {
            _left = new LinkwitzRileyCrossover(sampleRate, crossoverHz);
            _right = new LinkwitzRileyCrossover(sampleRate, crossoverHz);
        }

        public void SetCrossover(double crossoverHz)
        {
            _left.SetCrossover(crossoverHz);
            _right.SetCrossover(crossoverHz);
        }

        public void ProcessFrame(float left, float right,
            out float lowLeft, out float lowRight, out float highLeft, out float highRight)
        {
            _left.Process(left, out double ll, out double hl);
            _right.Process(right, out double lr, out double hr);

            lowLeft = (float)ll;
            lowRight = (float)lr;
            highLeft = (float)hl;
            highRight = (float)hr;
        }

        public void Reset()
        {
            _left.Reset();
            _right.Reset();
        }
    }
}
