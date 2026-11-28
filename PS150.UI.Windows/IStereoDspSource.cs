using PS150.Core.Mixers;
using PS150.Core.Equalizers;

namespace PS150.UI.Windows
{
    /// <summary>
    /// krok105: společné rozhraní pro přehrávače, co mají hotový STEREO
    /// mixpult+ekvalizér+analyzér (levý/pravý kanál zvlášť) - AudioPlayer
    /// (.mp3 a ostatní zvuky přes NAudio) a GmPianoMidiPlayer (.mid soubory,
    /// od kroku 104 taky přes sdílený mixpult+EQ, viz tam).
    ///
    /// Obě třídy mají už dnes přesně tahle veřejná pole/vlastnosti (stejná
    /// jména, stejné typy) - tohle rozhraní jen dovoluje FileBrowserWindow
    /// ovládat OBA přehrávače STEJNÝM kódem (viz ActiveStereoSource/
    /// ActiveLeft/ActiveLeftEq32 atd.), místo aby .mid a .mp3 měly dvě
    /// oddělené sady polí/formulářů pro to samé.
    /// </summary>
    public interface IStereoDspSource
    {
        /// <summary>Kanálový pás levého kanálu (Gain/HPF/Gate/Comp/EQ) - null, dokud nehraje stereo zdroj.</summary>
        MonoChannel? LeftChannelStrip { get; }

        /// <summary>Totéž pro pravý kanál.</summary>
        MonoChannel? RightChannelStrip { get; }

        /// <summary>Skutečný 32pásmový ekvalizér levého kanálu - null, dokud nehraje stereo zdroj.</summary>
        GraphicEqualizer32Band? LeftEq32 { get; }

        /// <summary>Totéž pro pravý kanál.</summary>
        GraphicEqualizer32Band? RightEq32 { get; }

        /// <summary>Skutečný spektrální analyzér levého kanálu - null, dokud nehraje stereo zdroj.</summary>
        SpectrumAnalyzer32Band? LeftAnalyzer32 { get; }

        /// <summary>Totéž pro pravý kanál.</summary>
        SpectrumAnalyzer32Band? RightAnalyzer32 { get; }

        /// <summary>Živé místo odběru obou analyzérů - false = za ekvalizérem, true = před ním (viz StereoMonoChannelSampleProvider.AnalyzerTapPreEq).</summary>
        bool AnalyzerTapPreEq { get; set; }
    }
}
