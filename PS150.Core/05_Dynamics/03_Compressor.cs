using System;

namespace PS150.Core.Dynamics
{
    /// <summary>
    /// krok87: standardní "feed-forward" kompresor s tvrdým kolenem (hard knee) -
    /// nad prahem (ThresholdDb) zeslabí přebytek o poměr Ratio:1, pod prahem nechá
    /// signál beze změny. Detekce hladiny přímo přes EnvelopeFollower s Attack/
    /// Release časy kompresoru (zjednodušené, ale běžně používané zapojení -
    /// obálka detektoru SE ROVNOU chová jako "rychlost komprese", žádná druhá
    /// vyhlazovací vrstva navíc).
    ///
    /// Výchozí ThresholdDb = 0 (nad plným rozsahem signálu -> prakticky nikdy
    /// nesepne = kompresor "vypnutý"), stejná konvence jako HPF=0/Gate=-120dB
    /// u MonoChannel.
    /// </summary>
    public sealed class Compressor
    {
        private readonly EnvelopeFollower _detector;

        /// <summary>Práh v dB - nad ním se komprimuje. 0 = prakticky vypnuto (výchozí).</summary>
        public double ThresholdDb { get; set; } = 0.0;

        private double _ratio = 4.0;

        /// <summary>Poměr komprese (4 = klasické 4:1). Minimálně 1 (= žádná komprese).</summary>
        public double Ratio
        {
            get => _ratio;
            set => _ratio = Math.Max(1.0, value);
        }

        /// <summary>Dorovnávací zisk po kompresi (kompenzuje ztrátu hlasitosti), v dB.</summary>
        public double MakeupGainDb { get; set; } = 0.0;

        /// <summary>Jak rychle kompresor nastoupí (ms), když hladina překročí práh.</summary>
        public double AttackMs
        {
            get => _detector.AttackMs;
            set => _detector.AttackMs = value;
        }

        /// <summary>Jak rychle kompresor povolí (ms), když hladina klesne pod práh.</summary>
        public double ReleaseMs
        {
            get => _detector.ReleaseMs;
            set => _detector.ReleaseMs = value;
        }

        public Compressor(double sampleRate)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            // krok87: rozumné výchozí časy pro obecné použití (hlas/varhany) -
            // rychlý nástup, pomalejší povolení, ať komprese "dýchá" plynule.
            _detector = new EnvelopeFollower(sampleRate, attackMs: 10.0, releaseMs: 100.0);
        }

        /// <summary>Zpracuje jeden vzorek - detekuje hladinu, spočítá a aplikuje zeslabení nad prahem.</summary>
        public double Process(double x)
        {
            double envelope = _detector.Process(Math.Abs(x));
            double envelopeDb = envelope <= 0.000001 ? -120.0 : 20.0 * Math.Log10(envelope);

            double gainReductionDb = 0.0;
            if (envelopeDb > ThresholdDb)
            {
                double overDb = envelopeDb - ThresholdDb;
                gainReductionDb = overDb * (1.0 - 1.0 / _ratio);   // standardní vzorec komprese nad prahem
            }

            double gainLinear = Math.Pow(10.0, (-gainReductionDb + MakeupGainDb) / 20.0);
            return x * gainLinear;
        }

        /// <summary>Vynuluje vnitřní stav detektoru - např. po přeskočení v nahrávce.</summary>
        public void Reset() => _detector.Reset();
    }
}
