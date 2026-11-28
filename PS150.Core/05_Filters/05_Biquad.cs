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

        /// <summary>
        /// krok96: pásmová propust (propouští jen úzké okolí centerHz, mimo něj
        /// zeslabuje k nule na obě strany) - recept "BPF, constant 0 dB peak gain"
        /// z "Audio EQ Cookbook". Na rozdíl od SetPeakingEq (ta jen zesílí/zeslabí,
        /// mimo pásmo nechá signál beze změny) tahle propust OSTATNÍ kmitočty
        /// potlačí. Používá se pro bance filtrů spektrálního analyzéru (viz
        /// PS150.Core.Equalizers.SpectrumAnalyzer32Band) - žádná FFT, jen 32
        /// takových propustí vedle sebe (paralelně), každá na jedno pásmo.
        /// </summary>
        public void SetBandPass(double sampleRate, double centerHz, double q = 1.0)
        {
            Prepare(sampleRate, centerHz, q, out double cosw, out double alpha);

            double b0 = alpha;
            double b1 = 0.0;
            double b2 = -alpha;
            Finish(b0, b1, b2, 1.0 + alpha, -2.0 * cosw, 1.0 - alpha);
        }

        /// <summary>
        /// krok82: zvonová (peaking) EQ křivka - zesílí/zeslabí jen okolí centerHz o
        /// gainDb, mimo pásmo nechá signál beze změny (na rozdíl od LowPass/HighPass,
        /// co jsou "propusti"). Q určuje šířku zvonu (vyšší Q = užší/ostřejší).
        /// Používá se pro parametrické EQ na kanálu mixáku i pro jedno pásmo
        /// grafického ekvalizéru (viz PS150.Core.Equalizers). Stejný recept
        /// "Audio EQ Cookbook" jako SetLowPass/SetHighPass výš.
        ///
        /// krok90: centerHz/q mimo platný rozsah (typicky nejvyšší pásmo
        /// 32pásmového ekvalizéru - F10 = 22350,6Hz - u souboru s běžným
        /// 44,1kHz vzorkováním, kde je Nyquist jen 22050Hz) se TICHO OŘÍZNOU,
        /// místo aby metoda vyhodila výjimku. Důvod: tahle metoda se volá i
        /// z vykreslovacího vlákna zvukové karty (WASAPI render callback,
        /// viz StereoMonoChannelSampleProvider.Read) - nezachycená výjimka tam
        /// není nic jako chybové hlášení, rovnou shodí CELOU aplikaci bez
        /// jakéhokoliv dialogu (potvrzeno crash.log: "AppDomain.
        /// UnhandledException, nejspíš vlákno MIDI/zvuku"). Oříznutý kmitočet
        /// leží těsně pod Nyquistem, takže takové pásmo má prakticky nulový/
        /// zanedbatelný slyšitelný efekt - to je v pořádku, daný kmitočet se
        /// do vzorkovaného signálu stejně nevejde.
        /// </summary>
        public void SetPeakingEq(double sampleRate, double centerHz, double gainDb, double q = 1.0)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            double nyquist = sampleRate / 2.0;
            double safeHz = Math.Clamp(centerHz, 1.0, Math.Max(1.0, nyquist - 1.0));
            double safeQ = Math.Max(0.05, q);

            double amplitude = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * safeHz / sampleRate;
            double cosw = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * safeQ);

            double b0 = 1.0 + alpha * amplitude;
            double b1 = -2.0 * cosw;
            double b2 = 1.0 - alpha * amplitude;
            double a0 = 1.0 + alpha / amplitude;
            double a1 = -2.0 * cosw;
            double a2 = 1.0 - alpha / amplitude;
            Finish(b0, b1, b2, a0, a1, a2);
        }

        /// <summary>krok90: stejné zdůvodnění oříznutí místo výjimky jako u SetPeakingEq výš.</summary>
        private static void Prepare(double sampleRate, double cutoffHz, double q, out double cosw, out double alpha)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            double nyquist = sampleRate / 2.0;
            double safeHz = Math.Clamp(cutoffHz, 1.0, Math.Max(1.0, nyquist - 1.0));
            double safeQ = Math.Max(0.05, q);

            double w0 = 2.0 * Math.PI * safeHz / sampleRate;
            cosw = Math.Cos(w0);
            alpha = Math.Sin(w0) / (2.0 * safeQ);
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
