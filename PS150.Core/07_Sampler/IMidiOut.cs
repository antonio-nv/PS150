using System;
using Melanchall.DryWetMidi.Core;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok18: společné rozhraní výstupu pro přehrávač .mid souborů - buď vlastní sampler
    /// (Sf2MidiSynth), nebo záložní Microsoft GS Wavetable Synth. Přehrávač jen posílá MIDI události.
    /// </summary>
    public interface IMidiOut : IDisposable
    {
        /// <summary>Krátký popis zdroje zvuku pro obrazovku (např. "MuseScore_General (SF2)").</summary>
        string Description { get; }

        void SendEvent(MidiEvent midiEvent);
    }
}
