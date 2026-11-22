using System;

namespace PS150.Core.Filters
{
    /// <summary>
    /// Biquad (filtr 2. řádu) podle receptu "Audio EQ Cookbook" (R. Bristow-Johnson).
    /// Počítá v double, aby výhybka s nízkým dělicím kmitočtem při 48 kHz nešuměla
    /// a nebyla nestabilní. Změna kmitočtu za chodu (SetLowPass / SetHighPass)
    /// zachová vnitřní stav filtru, takže se při ní neozve lupnutí.
    /// </summary>
    public sealed class Biquad
    {
        private double _b0 = 1.0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        /// <summary>Dolní propust (propouští kmitočty pod fc). q = 0,7071 je Butterworth.</summary>
        public void SetLowPass(double sampleRate, double cutoffHz, double q = 0.70710678118654752)
        {
            Prepare(sampleRate, cutoffHz, q, out double cosw, out double alpha);

            double b0 = (1.0 - cosw) / 2.0;
            double b1 = 1.0 - cosw;
            double b2 = (1.0 - cosw) / 2.0;
            Finish(b0, b1, b2, 1.0 + alpha, -2.0 * cosw, 1.0 - alpha);
        }

        /// <summary>Horní propust (propouští kmitočty nad fc).</summary>
        public void SetHighPass(double sampleRate, double cutoffHz, double q = 0.70710678118654752)
        {
            Prepare(sampleRate, cutoffHz, q, out double cosw, out double alpha);

            double b0 = (1.0 + cosw) / 2.0;
            double b1 = -(1.0 + cosw);
            double b2 = (1.0 + cosw) / 2.0;
            Finish(b0, b1, b2, 1.0 + alpha, -2.0 * cosw, 1.0 - alpha);
        }

        private static void Prepare(double sampleRate, double cutoffHz, double q, out double cosw, out double alpha)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (cutoffHz <= 0.0 || cutoffHz >= sampleRate / 2.0)
                throw new ArgumentOutOfRangeException(nameof(cutoffHz), "Dělicí kmitočet musí ležet mezi 0 a polovinou vzorkovacího kmitočtu.");
            if (q <= 0.0) throw new ArgumentOutOfRangeException(nameof(q));

            double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
            cosw = Math.Cos(w0);
            alpha = Math.Sin(w0) / (2.0 * q);
        }

        private void Finish(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        /// <summary>Zpracuje jeden vzorek (transponovaná přímá forma II).</summary>
        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        /// <summary>Vynuluje paměť filtru (ticho).</summary>
        public void Reset()
        {
            _z1 = 0.0;
            _z2 = 0.0;
        }
    }
}
