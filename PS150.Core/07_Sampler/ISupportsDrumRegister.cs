namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok37: obdoba ISupportsMelodicRegister, ale pro bicí (kanál D10) - umožňuje
    /// ZA BĚHU vynutit konkrétní bicí sadu (kit) podle "lidského" GM/GS čísla
    /// 1-128 (1 = Standard kit P000, staré natvrdo zapsané chování), nebo 0 =
    /// hraj skutečnou sadu podle Program Change na kanálu D10 v souboru.
    ///
    /// Implementuje Sf2MidiSynth (banky typu MuseScore_General mívají v bance
    /// 128 víc sad podle GS konvence - P000 Standard, P008 Room, P016 Power,
    /// P024 Electronic, P025 TR-808, P032 Jazz, P040 Brush, P048 Orchestra) a
    /// DualEngineMidiOut (předává dál svému _drums subenginu).
    ///
    /// OplMidiSynth (banka SoundBlaster) TOHLE NEIMPLEMENTUJE - formát .op2 má
    /// jen jednu univerzální bicí mapu (47 položek), žádné přepínání sad v něm
    /// není - viz Op2Bank.
    /// </summary>
    public interface ISupportsDrumRegister
    {
        void SetDrumProgram(int drumProgram);
    }
}
