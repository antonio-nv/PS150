namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok34: enginy (dnes Sf2MidiSynth), které umí ZA BĚHU přepnout univerzální
    /// tónový rejstřík - viz GmPianoMidiPlayer.SetMidiMelodicRegister.
    /// universalProgram: 0 = skutečné nástroje podle Program Change v souboru,
    /// 1-128 = vynucený GM program na všech netbicích kanálech ("lidské" číslování).
    /// </summary>
    public interface ISupportsMelodicRegister
    {
        void SetUniversalProgram(int universalProgram);
    }
}
