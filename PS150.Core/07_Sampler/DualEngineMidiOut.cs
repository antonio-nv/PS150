using Melanchall.DryWetMidi.Core;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok30: spojí dva nezávislé IMidiOut do jednoho - kanál D10 (bicí, index 9)
    /// jde do jednoho enginu, všechny ostatní kanály do druhého. Používá se v
    /// GmPianoMidiPlayer pro nezávislý výběr banky pro bicí vs. pro tóny u
    /// přehrávání .mid souborů (viz AvailablePlaybackBanks / AvailableDrumBanks).
    ///
    /// Žádné ruční mixování zvuku tu není potřeba: oba vnitřní enginy (typicky
    /// Sf2MidiSynth) si hrají každý na svůj vlastní WaveOutEvent/výstup, stejně
    /// jako kdyby hrály dvě různé aplikace najednou - zvuková karta/Windows je
    /// smíchá sama.
    /// </summary>
    public sealed class DualEngineMidiOut : IMidiOut, ISupportsMelodicRegister
    {
        // Kanál D10 podle GM standardu je index 9 (kanály se číslují od 0) -
        // stejná konvence jako Sf2MidiSynth.DrumChannel / GmPianoMidiPlayer.PercussionChannel.
        private const int DrumChannel = 9;

        private readonly IMidiOut _tonal;
        private readonly IMidiOut _drums;

        public DualEngineMidiOut(IMidiOut tonal, IMidiOut drums)
        {
            _tonal = tonal;
            _drums = drums;
        }

        /// <summary>Když obě banky jedou stejný engine, ukáže se jen jednou; jinak "tón: X / bicí: Y".</summary>
        public string Description => _tonal.Description == _drums.Description
            ? _tonal.Description
            : $"tón: {_tonal.Description} / bicí: {_drums.Description}";

        public void SendEvent(MidiEvent midiEvent)
        {
            int channel;
            switch (midiEvent)
            {
                case NoteOnEvent n: channel = (int)n.Channel; break;
                case NoteOffEvent n: channel = (int)n.Channel; break;
                case ControlChangeEvent n: channel = (int)n.Channel; break;
                case PitchBendEvent n: channel = (int)n.Channel; break;
                case ProgramChangeEvent n: channel = (int)n.Channel; break;
                default:
                    // Událost bez kanálu (meta, sysex...) - oba enginy ji stejně
                    // ignorují, stačí ji poslat kterémukoliv jednomu.
                    _tonal.SendEvent(midiEvent);
                    return;
            }

            (channel == DrumChannel ? _drums : _tonal).SendEvent(midiEvent);
        }

        public void Dispose()
        {
            _tonal.Dispose();
            _drums.Dispose();
        }

        /// <summary>krok34: přepošle na tónový engine (bicí kanál D10 se univerzálním rejstříkem netýká).</summary>
        public void SetUniversalProgram(int universalProgram)
        {
            if (_tonal is ISupportsMelodicRegister supported) supported.SetUniversalProgram(universalProgram);
        }
    }
}
