namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok46: společné rozhraní pro špičkovou hlasitost (VU metr) a stav
    /// přebuzení u enginů .mid přehrávače (Sf2MidiSynth, OplMidiSynth,
    /// SyntheticMidiSynth, DualEngineMidiOut) - stejný princip jako
    /// AudioEngine.ReadPeak()/ClipDetected u živého hraní (viz
    /// PS150.Core/06_Output/Audio.cs), jen stereo (levý/pravý) místo mono.
    ///
    /// ReadPeakLevels vrací špičku OD POSLEDNÍHO volání a vynuluje ji - stejný
    /// "vyzvedni a vynuluj" princip jako AudioEngine.ReadPeak()/
    /// AudioPlayer.ReadPeakLevels().
    ///
    /// ClipDetected je taky "vyzvedni a vynuluj" (na rozdíl od ToneEngine,
    /// kde je to okamžitý stav posledního vzorku) - Read() enginů se volá
    /// mnohem častěji, než se UI ptá (Tick()), takže kdyby se nic
    /// nezapamatovalo mezi dotazy, krátké přebuzení v jednom bufferu by UI
    /// klidně nikdy nezachytilo.
    /// </summary>
    public interface IProvidesAudioMeter
    {
        (float left, float right) ReadPeakLevels();
        bool ClipDetected { get; }
    }
}
