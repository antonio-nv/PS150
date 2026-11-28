using System;

namespace PS150.Core.Mixers
{
    /// <summary>
    /// krok85: mixpultový kanál pro STEREO vstup (dva nezávislé kanály, L a P) a
    /// stereo výstup - typicky zvuková karta PC (line-in), nebo jakýkoliv jiný
    /// zdroj, co je od začátku stereo. Má Gain (společný pro oba kanály) a
    /// Balance (NE Pan - to je důležitý rozdíl, viz níž).
    ///
    /// Balance vs. Pan: Pan (viz MonoToStereoChannel) umisťuje JEDEN mono zdroj
    /// do stereo pole, takže uprostřed musí oba výstupní kanály dohromady znít
    /// stejně hlasitě jako na kraji (odtud ta "constant power" sin/cos křivka).
    /// Balance naproti tomu jen vyvažuje hlasitost DVOU UŽ NEZÁVISLÝCH kanálů -
    /// nic se nikam "nepřesouvá", jen se ta "nefavorizovaná" strana ztlumí.
    /// Uprostřed (balance=0) zůstávají oba kanály na plné (nezměněné) hlasitosti -
    /// žádná -3dB kompenzace, protože žádná není potřeba.
    /// </summary>
    public sealed class StereoChannel
    {
        public const double MinGainDb = -24.0;
        public const double MaxGainDb = 24.0;

        private double _gainDb;
        private double _gainLinear = 1.0;

        /// <summary>Gain: standardní převod dB -> lineární násobič (10^(dB/20)), stejný pro L i P.</summary>
        public double GainDb
        {
            get => _gainDb;
            set
            {
                _gainDb = Math.Clamp(value, MinGainDb, MaxGainDb);
                _gainLinear = Math.Pow(10.0, _gainDb / 20.0);
            }
        }

        private double _balance;   // -1 (jen L) .. 0 (střed, oba beze změny) .. +1 (jen P)
        private double _balanceGainLeft = 1.0;
        private double _balanceGainRight = 1.0;

        /// <summary>
        /// Balance: -1 ztlumí pravý kanál na ticho (levý beze změny), +1 ztlumí levý
        /// (pravý beze změny), 0 nechá oba beze změny. Prostá lineární "ztlumit tu
        /// druhou stranu" křivka - na rozdíl od Pan žádná kompenzace hlasitosti.
        /// </summary>
        public double Balance
        {
            get => _balance;
            set
            {
                _balance = Math.Clamp(value, -1.0, 1.0);
                _balanceGainLeft = _balance <= 0.0 ? 1.0 : 1.0 - _balance;
                _balanceGainRight = _balance >= 0.0 ? 1.0 : 1.0 + _balance;
            }
        }

        /// <summary>Zpracuje jeden pár (L, P) vzorků - aplikuje Gain a Balance, vrátí pár (L, P).</summary>
        public (float Left, float Right) Process(float left, float right)
        {
            float g = (float)_gainLinear;
            return (left * g * (float)_balanceGainLeft, right * g * (float)_balanceGainRight);
        }
    }
}
