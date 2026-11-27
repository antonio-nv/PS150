namespace PS150.Core.Generators
{
    /// <summary>
    /// krok58: "hlas", co nic negeneruje - pro rejstříky, jejichž skutečný zvuk
    /// přichází odjinud (dnes jen banka 8 - OPL3/SoundBlaster, viz OplMidiSynth,
    /// mixovaný přímo v AudioEngine.Read/AttachLiveOpl, NE přes ToneEngine).
    ///
    /// Potřeba proto, že ToneEngine.NoteOn vytváří pro KAŽDÝ aktivní rejstřík
    /// jeden SynthVoice (viz CreateVoice) - bez týhle třídy by banka 8 musela
    /// buď úplně chybět v _activeNotes (a tedy by se nedala zobrazit/zapnout
    /// stejným způsobem jako ostatní rejstříky), nebo by omylem skončila na
    /// výchozím OrganVoice a hrála by NAVÍC vlastní (špatný) zvuk k tomu
    /// skutečnému z OPL3. SilentVoice jen "drží místo" - CalculateWaveform
    /// vrací 0.0, takže do mixu nepřidá vůbec nic.
    /// </summary>
    internal sealed class SilentVoice : SynthVoice
    {
        public SilentVoice(double sampleRate) : base(sampleRate) { }

        protected override double CalculateWaveform(double frequency) => 0.0;
    }
}
