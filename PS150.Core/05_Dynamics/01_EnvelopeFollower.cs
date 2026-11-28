using System;

namespace PS150.Core.Dynamics
{
    /// <summary>
    /// krok87: obálkový sledovač (envelope follower) se standardní "attack/release"
    /// balistikou - exponenciální vyhlazení absolutní hodnoty signálu, s jinou
    /// časovou konstantou pro nástup (Attack, když hladina roste) a jinou pro
    /// doznívání (Release, když klesá). Bez toho by Gate/Comp reagovaly na každý
    /// jednotlivý vzorek (vlnu signálu), ne na jeho skutečnou "hlasitost" - a při
    /// zapínání/vypínání by lupaly.
    ///
    /// Používá se jako detektor hladiny pro NoiseGate i Compressor (viz tam) -
    /// obě dynamické zpracování potřebují vědět, "jak hlasitě to teď hraje",
    /// vyhlazené stejným způsobem.
    /// </summary>
    public sealed class EnvelopeFollower
    {
        private readonly double _sampleRate;
        private double _envelope;

        private double _attackMs;
        private double _releaseMs;
        private double _attackCoeff;
        private double _releaseCoeff;

        /// <summary>Jak rychle obálka stoupá, když hladina roste (v milisekundách).</summary>
        public double AttackMs
        {
            get => _attackMs;
            set { _attackMs = Math.Max(0.01, value); RecalcCoeffs(); }
        }

        /// <summary>Jak rychle obálka klesá, když hladina klesá (v milisekundách).</summary>
        public double ReleaseMs
        {
            get => _releaseMs;
            set { _releaseMs = Math.Max(0.01, value); RecalcCoeffs(); }
        }

        public EnvelopeFollower(double sampleRate, double attackMs, double releaseMs)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;
            _attackMs = Math.Max(0.01, attackMs);
            _releaseMs = Math.Max(0.01, releaseMs);
            RecalcCoeffs();
        }

        private void RecalcCoeffs()
        {
            // krok87: standardní vzorec exponenciálního vyhlazení s "poločasem"
            // daným attack/release časem - čím kratší čas, tím rychleji se
            // koeficient (blíž nule) obálka přizpůsobí nové hodnotě.
            _attackCoeff = Math.Exp(-1.0 / (_attackMs / 1000.0 * _sampleRate));
            _releaseCoeff = Math.Exp(-1.0 / (_releaseMs / 1000.0 * _sampleRate));
        }

        /// <summary>Zpracuje jeden vzorek - dej mu Math.Abs(x), vrátí aktuální vyhlazenou obálku.</summary>
        public double Process(double absoluteSample)
        {
            double coeff = absoluteSample > _envelope ? _attackCoeff : _releaseCoeff;
            _envelope = coeff * _envelope + (1.0 - coeff) * absoluteSample;
            return _envelope;
        }

        /// <summary>Vynuluje obálku (ticho) - např. po přeskočení v nahrávce.</summary>
        public void Reset() => _envelope = 0.0;
    }
}
