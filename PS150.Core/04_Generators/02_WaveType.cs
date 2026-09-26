namespace PS150.Core.Generators
{
    public enum WaveType
    {
        Sine,       // Čistý sinus (základní tón)
        Sawtooth,   // Pila (bohatá na sudé i liché harmonické - smyčce, žestě)
        Square,     // Čtverec (liché harmonické - klarinet, 8-bit zvuky)
        Triangle,   // Trojúhelník (jemný tón, flétna)
        WhiteNoise  // Bílý šum (perkuse, fuk varhan)
    }

    // Který "zvukový engine" má preset použít. Výchozí je Organ, takže všechny
    // dosavadní presety (Bombard, Piano...) zůstávají beze změny funkční.
    public enum InstrumentType
    {
        Organ,
        Piano,
        Cembalo,
        Bell,
        AdditiveString, // Provizorní aditivní (alikvotová) struna - viz AdditiveStringVoice.cs
        Drums           // Bicí GM (35-81) - viz DrumVoice.cs a DrumParameter níž
    }

    // Kterou ze dvou metod má DrumVoice použít pro daný bicí zvuk (viz DrumParameter).
    public enum DrumNoiseMethod
    {
        WideBandpass,   // Bílý šum -> dolní propust (LowHz) + horní propust (HighHz) v sérii - široké, "kartáčovité" pásmo (činely, hi-hat, virbl...)
        NarrowBandpass  // Bílý šum -> rezonanční úzkopásmový filtr (CenterHz, Q) - tónově rozeznatelný "bouchanec" (bubny, tomy, konga...)
    }

    // Jeden bicí zvuk v rámci rejstříku 700 (viz VoicePreset.DrumParameters a
    // _700_Drums.cs). GmNoteNumber je to, PODLE ČEHO se v DrumVoice vybírá
    // (číslo MIDI noty na kanálu D10, 35-81), ne podle frekvence noty -
    // bicí totiž nemají výšku tónu jako ostatní nástroje, číslo noty přímo
    // VYBÍRÁ zvuk (kopák/virbl/hi-hat...), nepřepočítává se na kmitočet.
    public struct DrumParameter
    {
        public int GmNoteNumber;      // 35-81, viz General MIDI Percussion Key Map
        public string Name;           // Krátký název pro zobrazení, např. "Ac Bass Drum"
        public string Abbreviation;   // 4 znaky pro úzké zobrazení (např. notová osnova v přehrávači souborů) - např. "LoTo" pro Low Tom
        public DrumNoiseMethod Method;

        // Pro Method == WideBandpass:
        public double LowHz;
        public double HighHz;

        // Pro Method == NarrowBandpass:
        public double CenterHz;
        public double Q;

        // Společná hlasitost výsledného zvuku (obě metody), 0.0-1.0+.
        public double Amplitude;
    }

    public class VoicePreset
    {
        public string Name { get; set; } = "Default";

        // Číslo rejstříku odpovídající fyzickému číslování na hracím stole
        // (viz štítky na klapkách skutečného nástroje, např. "68 - Dolce 8'").
        // 0 = zatím nepřiřazeno.
        public int Number { get; set; } = 0;

        // Který nástroj/engine preset používá.
        public InstrumentType Instrument { get; set; } = InstrumentType.Organ;

        // Ladění (temperatura) tohoto konkrétního rejstříku - viz Temperament.cs.
        // Výchozí je obyčejná rovnoměrná temperatura (Equal), stejná pro
        // všechny tóny/rejstříky jako doteď. Každý rejstřík si ale teď může
        // nést svou VLASTNÍ temperaturu (Temperament.QuarterCommaMeantone,
        // Temperament.MelzerGeorgKratkyI, nebo si klidně napiš vlastní) -
        // viz ToneEngine.NoteOn, kde se používá per-rejstřík, ne globálně.
        public Temperament Temperament { get; set; } = Temperament.Equal;

        // SJEDNOCENÁ tabulka alikvót pro VŠECHNY nástroje (Organ/Bell/
        // AdditiveString) - poměr kmitočtu, hlasitost, a volitelně rychlost
        // doznívání toho konkrétního alikvotu.
        //
        // PartialDecayRates je NEPOVINNÉ (smí zůstat null) - použij ho jen
        // tam, kde KAŽDÝ alikvot doznívá svou vlastní rychlostí i během
        // "sustain" (úder/brnknutí/zvon - BellVoice, AdditiveStringVoice).
        // OrganVoice ho ignoruje úplně, protože foukaná píšťala hraje na
        // konstantní hlasitosti, dokud se drží klávesa - celý tón zhasne
        // najednou přes ADSR Release, ne alikvot po alikvotu.
        //
        // PianoVoice a CembaloVoice nečtou ani jedno z těchhle polí - mají
        // svou barvu zadrátovanou přímo ve fyzikálním modelu strun
        // (Karplus-Strong).
        public double[] PartialRatios { get; set; } = null;
        public double[] PartialAmplitudes { get; set; } = null;
        public double[] PartialDecayRates { get; set; } = null;

        // Parametry Šumu / Chiffu / Úderu (Organ)
        public double ChiffNoiseGain { get; set; } = 0.2;
        public double ChiffFilterFreqHz { get; set; } = 800.0;
        public double ChiffFilterQ { get; set; } = 1.0;
        public double ChiffDurationSec { get; set; } = 0.030; // 30ms

        // Modulace (Vibrato / Tremolo) - kmitající v čase, viz OrganVoice/BellVoice.
        public ModulationType ModType { get; set; } = ModulationType.None;
        public double ModSpeedHz { get; set; } = 5.5;
        public double ModDepth { get; set; } = 0.08;

        // Statický chór (rozladění druhé kopie celé alikvotní řady, v CENTECH) -
        // viz OrganVoice.cs. 0 = vypnuto (jeden hlas, jako doteď). Tohle je ta
        // "STATICKÁ" varianta chóru (dvě pevné, nekmitající frekvence blízko
        // sebe) - přesně to, co bylo naměřeno u Casio STRINGS (zdvojené,
        // těsně u sebe ležící spektrální čáry). NENÍ to totéž jako ModType=AM/FM
        // (ty kmitají v čase, tady se jen jednou provždy rozladí druhá kopie).
        public double ChorusDetuneCents { get; set; } = 0.0;

        // Poměr hlasitosti mezi hlasem A (základní) a hlasem B (rozladěná
        // kopie), 0.0 = jen hlas A (chór fakticky vypnutý), 1.0 = oba stejně
        // hlasitě. Výchozí 0.5 dá vyrovnaný dvouhlasý chór.
        public double ChorusMix { get; set; } = 0.5;

        // NEPOVINNÉ: rozladění NEZÁVISLE pro každou harmonickou zvlášť (v
        // centech), stejně dlouhé pole jako PartialRatios. Když je vyplněné,
        // MÁ PŘEDNOST před jednotným ChorusDetuneCents - skutečné analogové
        // ensemble stroje totiž nerozlaďují celé spektrum jedním pevným
        // poměrem, ale každý oscilátor/harmonickou zvlášť, nezávisle (viz
        // naměřená data z Casio STRINGS - rozladění tam kolísalo od ~7 do
        // ~96 centů podle harmonické, ne jedna konstanta). Necháš-li null,
        // použije se jednotné ChorusDetuneCents jako doteď.
        public double[] PartialDetuneCentsB { get; set; } = null;

        // Volitelné přepsání ADSR obálky (útok/pokles/sustain/dozvuk) - funguje
        // pro JAKÝKOLIV Instrument (Organ/Piano/Cembalo/Bell). Když necháš null,
        // každý hlas použije své vestavěné výchozí hodnoty (jako dosud).
        public AdsrEnvelope Envelope { get; set; } = null;

        // --- Parametry pro Instrument == Drums (viz DrumVoice.cs) - jeden
        // rejstřík (typicky Number=700) nese CELOU bicí soupravu, zvuk se
        // vybírá podle GmNoteNumber (35-81), ne podle frekvence/noty jako
        // u ostatních nástrojů. ---
        public DrumParameter[] DrumParameters { get; set; } = null;

        // --- Parametry pro Instrument == Piano/Cembalo (fyzikální model struny,
        // Karplus-Strong - viz KarplusStrongString.cs). Organ/Bell tato pole
        // nepoužívají. ---

        // Za kolik sekund doznívá ZÁKLADNÍ tón o -60 dB. Vyšší harmonické
        // doznívají samy rychleji, netřeba nastavovat zvlášť.
        public double StringDecaySeconds { get; set; } = 8.0;

        // 0.0-0.98: síla dolní propusti ve zpožďovací smyčce struny.
        // Nižší = jasnější/ostřejší zvuk (cembalo), vyšší = tmavší/plnější (klavír).
        public double StringBrightness { get; set; } = 0.5;

        // 0.02-0.5: poloha úderu/drnknutí jako podíl délky struny od kraje
        // (viz KarplusStrongString.Excite). Blíž kraji = jasnější/"kovovější"
        // tón (cembalo), blíž středu (0.5) = měkčí, temnější tón (klavír).
        public double PickPosition { get; set; } = 0.125;

        // 0.0-1.0: kolik šumu se přimíchá k deterministickému tvaru výchylky
        // (textura úderu/drnknutí). Základní tón vždy nese ten deterministický
        // tvar, ne šum - tohle je jen na "dech"/špínu zvuku navrch.
        public double ExcitationNoiseAmount { get; set; } = 0.15;
    }

    public enum ModulationType
    {
        None,
        AM, // Amplitudová modulace (Tremolo)
        FM  // Frekvenční modulace (Vibrato)
    }

}
