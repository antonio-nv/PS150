using System;

namespace PS150.Core.Dynamics
{
    /// <summary>
    /// krok87: standardní šumová brána (noise gate) - pod prahem (ThresholdDb)
    /// signál ztiší na nulu, nad prahem ho pustí beze změny. Detekuje hladinu
    /// přes EnvelopeFollower (rychlý detektor, ne stejný jako vyhlazení samotné
    /// brány - viz níž), přechod mezi "zavřeno"/"otevřeno" vyhlazuje VLASTNÍ
    /// attack/release na zisku brány, aby nelupalo.
    ///
    /// Výchozí ThresholdDb = -120 (prakticky nikdy nezavře = brána "vypnutá") -
    /// stejná konvence jako HPF = 0 u MonoChannel: dokud se parametr nenastaví,
    /// chová se, jako by tam vůbec nebyl.
    /// </summary>
    public sealed class NoiseGate
    {
        private readonly double _sampleRate;
        private readonly EnvelopeFollower _detector;
        private double _gain;   // aktuální vyhlazená "otevřenost" brány, 0 (zavřeno) .. 1 (otevřeno)

        /// <summary>Práh v dB - pod ním brána zavírá. -120 = prakticky vypnuto (výchozí).</summary>
        public double ThresholdDb { get; set; } = -120.0;

        private double _attackMs = 2.0;
        private double _releaseMs = 150.0;
        private double _gainAttackCoeff;
        private double _gainReleaseCoeff;

        /// <summary>Jak rychle se brána otevírá (ms) - krátké, ať neusekne nástup tónu.</summary>
        public double AttackMs
        {
            get => _attackMs;
            set { _attackMs = Math.Max(0.1, value); RecalcGainCoeffs(); }
        }

        /// <summary>Jak rychle se brána zavírá (ms) - delší, ať neuřízne dozvuk.</summary>
        public double ReleaseMs
        {
            get => _releaseMs;
            set { _releaseMs = Math.Max(0.1, value); RecalcGainCoeffs(); }
        }

        public NoiseGate(double sampleRate)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;
            // krok87: detektor hladiny má vlastní, rychlejší balistiku než samotné
            // otevírání/zavírání brány (viz AttackMs/ReleaseMs výš) - detekce musí
            // reagovat rychle, aby brána věděla "už to hraje", i když se pak sama
            // otvírá/zavírá plynuleji.
            _detector = new EnvelopeFollower(sampleRate, attackMs: 1.0, releaseMs: 50.0);
            RecalcGainCoeffs();
        }

        private void RecalcGainCoeffs()
        {
            _gainAttackCoeff = Math.Exp(-1.0 / (_attackMs / 1000.0 * _sampleRate));
            _gainReleaseCoeff = Math.Exp(-1.0 / (_releaseMs / 1000.0 * _sampleRate));
        }

        /// <summary>Zpracuje jeden vzorek - detekuje hladinu, otevře/zavře bránu, vrátí ztlumený/propuštěný vzorek.</summary>
        public double Process(double x)
        {
            double envelope = _detector.Process(Math.Abs(x));
            double envelopeDb = envelope <= 0.000001 ? -120.0 : 20.0 * Math.Log10(envelope);

            double targetGain = envelopeDb >= ThresholdDb ? 1.0 : 0.0;
            double coeff = targetGain > _gain ? _gainAttackCoeff : _gainReleaseCoeff;
            _gain = coeff * _gain + (1.0 - coeff) * targetGain;

            return x * _gain;
        }

        /// <summary>Vynuluje vnitřní stav (brána zavřená, detektor na nule) - např. po přeskočení v nahrávce.</summary>
        public void Reset()
        {
            _gain = 0.0;
            _detector.Reset();
        }
    }
}
