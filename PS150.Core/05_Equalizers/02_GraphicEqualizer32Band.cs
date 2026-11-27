using System;
using PS150.Core.Filters;

namespace PS150.Core.Equalizers
{
    /// <summary>
    /// krok82: standardní 32pásmový grafický ekvalizér - 32 nezávislých "zvonových"
    /// (peaking) biquad filtrů zapojených v sérii, každý na jednom z kmitočtů
    /// GraphicEqBands.CenterFreqHz (viz tam). Zapojení v sérii je běžná praxe pro
    /// grafický ekvalizér - pořadí pásem na výsledku nic nemění (jde o lineární
    /// filtry), jen musí být všechna zapojená za sebou, ne paralelně.
    ///
    /// Q = odmocnina ze dvou (~1,414) je zvolená jako rozumný výchozí kompromis pro
    /// pásma vzdálená o velkou tercii (dost široký rozestup) - zvon ani zbytečně
    /// neprosakuje do sousedních pásem, ani není nepřirozeně úzký/zvonivý. Dá se to
    /// časem doladit sluchem, až to poběží na reálném zvuku.
    ///
    /// Tahle třída sama o sobě ještě není zapojená do přehrávání (to je další,
    /// samostatný krok - navázání na Audio.cs/ToneEngine) - je to jen hotový,
    /// nezávisle použitelný a testovatelný stavební kámen.
    /// </summary>
    public sealed class GraphicEqualizer32Band
    {
        private const double DefaultQ = 1.4142135623730951;   // krok82: odmocnina ze dvou

        private readonly Biquad[] _bands;
        private readonly double[] _gainDb = new double[GraphicEqBands.Count];
        private readonly double _sampleRate;

        public GraphicEqualizer32Band(double sampleRate)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;

            _bands = new Biquad[GraphicEqBands.Count];
            for (int i = 0; i < _bands.Length; i++)
            {
                _bands[i] = new Biquad();
                _bands[i].SetPeakingEq(_sampleRate, GraphicEqBands.CenterFreqHz[i], 0.0, DefaultQ);
            }
        }

        /// <summary>Nastaví zisk jednoho pásma v dB (kladně zesílí, záporně zeslabí) a hned přepočítá jeho filtr.</summary>
        public void SetBandGainDb(int band, double gainDb)
        {
            if (band < 0 || band >= _bands.Length) throw new ArgumentOutOfRangeException(nameof(band));
            _gainDb[band] = gainDb;
            _bands[band].SetPeakingEq(_sampleRate, GraphicEqBands.CenterFreqHz[band], gainDb, DefaultQ);
        }

        public double GetBandGainDb(int band) => _gainDb[band];

        /// <summary>Zpracuje jeden vzorek postupně přes všech 32 pásem v sérii.</summary>
        public double Process(double x)
        {
            double y = x;
            for (int i = 0; i < _bands.Length; i++)
                y = _bands[i].Process(y);
            return y;
        }

        /// <summary>Vynuluje paměť všech pásem (ticho) - např. po přeskočení v nahrávce.</summary>
        public void Reset()
        {
            foreach (var band in _bands)
                band.Reset();
        }
    }
}
