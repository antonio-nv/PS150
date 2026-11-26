namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok44: obdoba ISupportsMelodicRegister/ISupportsDrumRegister, ale pro
    /// vlastní syntézu (banky _001_SyntetickeZvuky / _009_SynteticBici, viz
    /// SyntheticMidiSynth). Na rozdíl od těch dvou se tu NEPŘEDÁVÁ "lidské" GM
    /// číslo 1-128 - jde rovnou o skutečné číslo rejstříku z ToneEngine
    /// (Melzer 1-200, dál 200/300/400/500/501/600...), proto je rozsah širší
    /// (1-999, viz FileBrowserWindow - pole UniR).
    ///
    /// Implementuje SyntheticMidiSynth (přes ToneEngine.SetSoleActiveRegister
    /// - zapne JEN tenhle rejstřík, vypne všechny ostatní, protože pro
    /// přehrávání .mid souborů nemá kombinování víc rejstříků najednou
    /// (tutti/pléno) smysl - to zůstává jen živému hraní) a DualEngineMidiOut
    /// (předává dál svému _tonal subenginu, stejně jako ISupportsMelodicRegister
    /// - kanál D10 se tímhle netýká).
    /// </summary>
    public interface ISupportsSyntheticRegister
    {
        void SetSyntheticRegister(int registerNumber);
    }
}
