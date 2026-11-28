using System;
using NAudio.Wave;
using PS150.Core.Equalizers;   // krok89: GraphicEqualizer32Band

namespace PS150.Core.Mixers
{
    /// <summary>
    /// krok88: NAudio "ISampleProvider" adaptér - vezme STEREO zdroj (2 kanály),
    /// rozdělí ho na L a P, KAŽDÝ kanál pošle přes VLASTNÍ, NEZÁVISLOU instanci
    /// MonoChannel (Gain/HPF/Gate/Comp/EQ - viz tam), a výsledek zase sesype zpátky
    /// do stereo páru. Použití: výstup ze samplovaných zvukových bank (ten je
    /// stereo) na cestě ke stereo zvukové kartě PC - viz PS150.UI.Windows.
    /// AudioPlayer.BuildOutput.
    ///
    /// Vlastní dvě VEŘEJNÉ instance MonoChannel (Left/Right), aby šlo zvenčí
    /// nastavovat Gain/HPF/Gate/Comp/EQ na obou stranách - buď stejně, nebo
    /// úplně nezávisle (třeba jiný HPF na levém a pravém kanálu).
    ///
    /// krok89: za každý MonoChannel navíc přibyla VEŘEJNÁ instance
    /// GraphicEqualizer32Band (LeftEq32/RightEq32) - skutečný 32pásmový
    /// ekvalizér (viz PS150.Core.Equalizers), odpovídající 32 zadávacím polím
    /// ve formuláři spektrálního analyzéru (mono i stereo verze, viz
    /// FileBrowserWindow.RenderEqBands/RenderEqBandsStereo). Pořadí ve
    /// zpracování: nejdřív MonoChannel (Gain/HPF/Gate/Comp/jednopásmové EQ),
    /// pak teprve 32pásmový ekvalizér - tónová korekce "navrch" nad
    /// dynamikou, ne naopak.
    ///
    /// krok96: za ekvalizérem navíc SpectrumAnalyzer32Band (LeftAnalyzer32/
    /// RightAnalyzer32) - skutečný spektrální analyzér (banka pásmových
    /// propustí + sledování úrovně, ŽÁDNÁ FFT, viz tam). PŮVODNĚ (krok96/97)
    /// natvrdo AŽ ZA ekvalizérem (měří výstup/výsledek úpravy, ne surový
    /// vstup) - to byla uživatelova PRVNÍ volba ("za ekvalizérem"). Analyzér
    /// signál jen MĚŘÍ, nic netvaruje (Process vrací vstup beze změny), takže
    /// jeho přidání nijak nemění to, co je slyšet.
    ///
    /// krok101: místo odběru je teď ŽIVĚ PŘEPÍNATELNÉ (viz AnalyzerTapPreEq/
    /// FileBrowserWindow klávesa P) - uživatel chtěl při "hrůzostrašných"
    /// nastaveních vidět i nezpracovaný signál PŘED ekvalizérem, ne jen
    /// výsledek za ním. DŮLEŽITÉ: tenhle přepínač mění jen to, co se MĚŘÍ -
    /// to, co skutečně HRAJE (buffer), zůstává VŽDY výstup ZA ekvalizérem,
    /// ať je AnalyzerTapPreEq jakkoli nastavené.
    /// </summary>
    public sealed class StereoMonoChannelSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        public MonoChannel Left { get; }
        public MonoChannel Right { get; }

        public GraphicEqualizer32Band LeftEq32 { get; }
        public GraphicEqualizer32Band RightEq32 { get; }

        public SpectrumAnalyzer32Band LeftAnalyzer32 { get; }
        public SpectrumAnalyzer32Band RightAnalyzer32 { get; }

        /// <summary>krok101: false (výchozí) = analyzér měří signál ZA ekvalizérem
        /// (výsledek úpravy), true = PŘED ekvalizérem (nezpracovaný signál). Nemění
        /// se tím, co hraje - jen co se měří/zobrazuje (viz Read níž).</summary>
        public bool AnalyzerTapPreEq { get; set; }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public StereoMonoChannelSampleProvider(ISampleProvider source)
        {
            if (source.WaveFormat.Channels != 2)
                throw new ArgumentException("Zdroj musí být stereo (2 kanály).", nameof(source));

            _source = source;
            double sampleRate = source.WaveFormat.SampleRate;
            Left = new MonoChannel(sampleRate);
            Right = new MonoChannel(sampleRate);
            LeftEq32 = new GraphicEqualizer32Band(sampleRate);
            RightEq32 = new GraphicEqualizer32Band(sampleRate);
            LeftAnalyzer32 = new SpectrumAnalyzer32Band(sampleRate);
            RightAnalyzer32 = new SpectrumAnalyzer32Band(sampleRate);
        }

        /// <summary>
        /// Přečte vzorky ze zdroje (prokládané L,P,L,P...) a KAŽDÝ zvlášť pošle
        /// přes svůj kanál (Left/Right), pak přes svůj 32pásmový ekvalizér
        /// (LeftEq32/RightEq32) a nakonec přes svůj spektrální analyzér
        /// (LeftAnalyzer32/RightAnalyzer32 - jen měří, signál nezmění), výsledek
        /// zapíše zpátky na stejné místo.
        ///
        /// krok101: analyzér dostane buď signál PŘED ekvalizérem (channelOut),
        /// nebo PO ekvalizérem (eqOut), podle AnalyzerTapPreEq - ale do bufferu
        /// (= co skutečně hraje) se VŽDY zapíše eqOut, bez ohledu na to, co se
        /// zrovna měří.
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            for (int i = 0; i + 1 < samplesRead; i += 2)
            {
                double leftChannelOut = Left.Process(buffer[offset + i]);
                double leftEqOut = LeftEq32.Process(leftChannelOut);
                LeftAnalyzer32.Process(AnalyzerTapPreEq ? leftChannelOut : leftEqOut);
                buffer[offset + i] = (float)leftEqOut;

                double rightChannelOut = Right.Process(buffer[offset + i + 1]);
                double rightEqOut = RightEq32.Process(rightChannelOut);
                RightAnalyzer32.Process(AnalyzerTapPreEq ? rightChannelOut : rightEqOut);
                buffer[offset + i + 1] = (float)rightEqOut;
            }

            return samplesRead;
        }
    }
}
