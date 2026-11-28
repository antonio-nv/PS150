using System;

namespace PS150.Core.Mixers
{
    /// <summary>
    /// krok82/85: SPECIALIZOVANÝ mixpultový kanál - jeden mono vstup, stereo výstup
    /// s Pan (umístění v stereo poli). Přejmenováno z původního MixerChannel
    /// (krok82), protože se ukázalo, že tohle NENÍ ten základní/výchozí typ kanálu -
    /// pro domácí přehrávání na reprobednách je výchozí MonoChannel (mono-mono,
    /// viz tam) nebo StereoChannel (stereo-stereo, viz tam). Tenhle typ se hodí
    /// spíš pro speciální případy jako sluchátkový výstup, kde dává smysl umístit
    /// mono zdroj (mikrofon, syntezátor, varhanní hlas) někam do stereo prostoru.
    ///
    /// Gain: standardní převod dB -> lineární násobič (10^(dB/20)).
    ///
    /// Pan: standardní "constant power" (stejná hlasitost) panoráma. Na krajích
    /// (-1/+1) zní zdroj jen z jednoho kanálu na plnou hlasitost, uprostřed (0)
    /// zní z obou kanálů, ale KAŽDÝ zeslabený o cca -3dB (ne -6dB jako u prostého
    /// lineárního rozdělení L=1-p, P=p) - jinak by se při otáčení panorámy zdálo,
    /// že uprostřed zvuk "propadne" (je tišší, než na krajích). Proto se použije
    /// sin/cos křivka: L = cos(úhel), P = sin(úhel), úhel 0..π/2 pro pan -1..+1.
    /// (Porovnej se StereoChannel.Balance, která žádnou kompenzaci nepoužívá -
    /// tam totiž žádná potřeba není, viz komentář tam.)
    /// </summary>
    public sealed class MonoToStereoChannel
    {
        public const double MinGainDb = -24.0;
        public const double MaxGainDb = 24.0;

        private double _gainDb;
        private double _gainLinear = 1.0;

        public double GainDb
        {
            get => _gainDb;
            set
            {
                _gainDb = Math.Clamp(value, MinGainDb, MaxGainDb);
                _gainLinear = Math.Pow(10.0, _gainDb / 20.0);
            }
        }

        private double _pan;   // -1 (úplně vlevo) .. 0 (střed) .. +1 (úplně vpravo)
        private double _panGainLeft = 0.70710678118654752;
        private double _panGainRight = 0.70710678118654752;

        public double Pan
        {
            get => _pan;
            set
            {
                _pan = Math.Clamp(value, -1.0, 1.0);
                double angle = (_pan + 1.0) * (Math.PI / 4.0);   // -1..+1 -> 0..π/2
                _panGainLeft = Math.Cos(angle);
                _panGainRight = Math.Sin(angle);
            }
        }

        /// <summary>Zpracuje jeden mono vzorek - aplikuje Gain a Pan, vrátí pár (L, P).</summary>
        public (float Left, float Right) Process(float monoSample)
        {
            float sample = monoSample * (float)_gainLinear;
            return (sample * (float)_panGainLeft, sample * (float)_panGainRight);
        }
    }
}
