using System;
using PS150.Core.Filters;
using PS150.Core.Dynamics;
using PS150.Core.VUMeters;

namespace PS150.Core.Equalizers
{
    /// <summary>
    /// krok96: skutečný spektrální analyzér pro 32 hudebních pásem - stejné
    /// kmitočty jako GraphicEqualizer32Band (viz GraphicEqBands), ale ŽÁDNÁ FFT
    /// ani DTFT. Místo toho banka 32 NEZÁVISLÝCH pásmových propustí (Biquad.
    /// SetBandPass), každá naladěná na jedno pásmo, a za KAŽDOU z nich vlastní
    /// EnvelopeFollower, co hlídá aktuální hladinu.
    ///
    /// Zásadní rozdíl oproti GraphicEqualizer32Band: tam jsou filtry v SÉRII
    /// (jeden vzorek postupně projde všech 32 pásem za sebou, protože se tím
    /// signál doopravdy tvaruje). Tady jsou v PARALELE - každé pásmo dostane
    /// STEJNÝ vstupní vzorek nezávisle na ostatních (jen MĚŘÍME, co v signálu je,
    /// netvarujeme ho).
    ///
    /// Přesně takhle fungovaly i skutečné hardwarové "rozsvěcovací" analyzéry u
    /// starých grafických ekvalizérů - banka filtrů a detektorů úrovně, ne FFT.
    /// Počítá se průběžně vzorek po vzorku, žádné bloky/okna, tedy ani žádné
    /// zpoždění navíc kvůli čekání na nasbírání vzorků.
    /// </summary>
    public sealed class SpectrumAnalyzer32Band
    {
        // krok98: Q analyzéru je ZÁMĚRNĚ nezávislé na Q ekvalizéru (GraphicEqualizer32Band
        // má vlastní DefaultQ=1.4142). U ekvalizéru je žádoucí široký, mírný "zvon"
        // (Q~1.4 ~ 1 oktáva šířky) - ladí se tím zvuk přirozeně, bez "chirurgických"
        // zásahů. U analyzéru je to naopak - jen MĚŘÍME, takže čím užší pásmo, tím
        // ostřejší a přesnější obrázek, a žádné "zvonění" do okolí nevadí.
        //
        // Q=8.6514 odpovídá šířce pásma cca 1/6 oktávy (pro srovnání: Q=1.4142 je
        // celá oktáva). Sousední pásmo (1/3 oktávy dál) je tak potlačené o ~12dB
        // místo dřívějších ~1,6dB - čistá sinusovka tedy rozsvítí hlavně "svůj"
        // sloupec a výrazně méně "rozlije" energii do sousedů.
        //
        // Daň za užší pásmo: pomalejší náběh na nejnižších kmitočtech (u 17Hz pásma
        // cca 150-300ms místo dřívějších 26ms) - pro vizuální metr ještě v pohodě
        // (podobné ballistice klasického VU metru), ale dál to zužovat už by analyzér
        // znatelně zpomalovalo oproti hudbě.
        private const double FilterQ = 8.6514;

        // krok96: balistika hladiny - rychlý nástup (ať je vidět i krátký ráz
        // v signálu), pomalejší doznívání (ať sloupec na displeji neposkakuje
        // a dá se v klidu přečíst) - stejná myšlenka jako u VU metru/Gate/Comp.
        private const double AttackMs = 10.0;
        private const double ReleaseMs = 250.0;

        private readonly Biquad[] _bandpass;
        private readonly EnvelopeFollower[] _envelopes;
        private readonly double[] _levelLinear = new double[GraphicEqBands.Count];
        private readonly double _sampleRate;

        // krok99: kmitočty NEJSOU napevno - viz UseFrequencyTable níž (živé přepnutí
        // hudební <-> ISO třetinooktávové tabulky, viz FileBrowserWindow._useIsoBands).
        private double[] _centerFreqHz = GraphicEqBands.CenterFreqHz;

        public SpectrumAnalyzer32Band(double sampleRate)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;

            _bandpass = new Biquad[GraphicEqBands.Count];
            _envelopes = new EnvelopeFollower[GraphicEqBands.Count];
            for (int i = 0; i < GraphicEqBands.Count; i++)
            {
                _bandpass[i] = new Biquad();
                _bandpass[i].SetBandPass(_sampleRate, _centerFreqHz[i], FilterQ);
                _envelopes[i] = new EnvelopeFollower(sampleRate, AttackMs, ReleaseMs);
            }
        }

        /// <summary>
        /// krok99: přeladí všech 32 pásem na JINOU tabulku kmitočtů, ŽIVĚ za chodu -
        /// stejné bezpečné přeladění jako u GraphicEqualizer32Band.UseFrequencyTable
        /// (jen přepočet koeficientů, žádná alokace/výjimka na zvukovém vlákně).
        /// Analyzér nemá žádný "zisk" k zachování (jen měří) - obálky (EnvelopeFollower)
        /// se nulovat nemusí, během pár desítek milisekund (AttackMs/ReleaseMs) samy
        /// doběhnou na správnou hladinu nového kmitočtu.
        /// </summary>
        public void UseFrequencyTable(double[] centerFreqHz)
        {
            if (centerFreqHz == null) throw new ArgumentNullException(nameof(centerFreqHz));
            if (centerFreqHz.Length != _bandpass.Length)
                throw new ArgumentException("Tabulka musí mít stejný počet pásem.", nameof(centerFreqHz));

            _centerFreqHz = centerFreqHz;
            for (int i = 0; i < _bandpass.Length; i++)
                _bandpass[i].SetBandPass(_sampleRate, _centerFreqHz[i], FilterQ);
        }

        /// <summary>
        /// Zpracuje jeden vzorek - KAŽDÉ z 32 pásem dostane STEJNÝ vstup (paralelní
        /// banka, na rozdíl od sériové GraphicEqualizer32Band). Vrací hodnotu
        /// vstupního vzorku beze změny (analyzér jen MĚŘÍ, nic netvaruje) - hodí se
        /// to pro zapojení do řetězce zpracování bez vedlejšího efektu na zvuk.
        /// </summary>
        public double Process(double x)
        {
            for (int i = 0; i < _bandpass.Length; i++)
            {
                double filtered = _bandpass[i].Process(x);
                _levelLinear[i] = _envelopes[i].Process(Math.Abs(filtered));
            }
            return x;
        }

        /// <summary>Aktuální hladina pásma v dB (-120..0dB), viz AudioMeter.LinearToDecibels.</summary>
        public double GetBandLevelDb(int band)
        {
            if (band < 0 || band >= _bandpass.Length) throw new ArgumentOutOfRangeException(nameof(band));
            return AudioMeter.LinearToDecibels((float)_levelLinear[band]);
        }

        /// <summary>Vynuluje paměť všech filtrů i obálek (ticho) - např. po přeskočení v nahrávce.</summary>
        public void Reset()
        {
            for (int i = 0; i < _bandpass.Length; i++)
            {
                _bandpass[i].Reset();
                _envelopes[i].Reset();
                _levelLinear[i] = 0.0;
            }
        }
    }
}
