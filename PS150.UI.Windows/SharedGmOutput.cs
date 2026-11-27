using System;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PS150.UI.Windows
{
    /// <summary>
    /// krok64: JEDNO sdílené spojení na "Microsoft GS Wavetable Synth" pro
    /// celou appku. Příčina pádu z crash.log (MidiDeviceException: "Device
    /// is already in use", OUT_OPENRESULT_ALLOCATED): App.xaml.cs (živé
    /// hraní, banka 002) i GmPianoMidiPlayer.GsWavetableOut (záložní
    /// přehrávač .mid, taky banka 002) si dřív KAŽDÝ zvlášť otevíraly VLASTNÍ
    /// OutputDevice.GetByName("Microsoft GS Wavetable Synth") - a tohle
    /// zařízení (na rozdíl od skutečného MIDI portu) Windows dovolí mít
    /// otevřené jen JEDNÍM handlem najednou. Kdo poslal první MIDI event,
    /// ten handle "vyhrál"; druhý dostal při svém prvním SendEvent()
    /// MidiDeviceException - a protože šlo o nezachycenou výjimku (v
    /// krok61 jsem na tohle riziko upozorňoval, ale neřešil), appka spadla.
    ///
    /// Řešení: jedno sdílené spojení, o které se dělí živé hraní i
    /// přehrávač .mid (uzamčené proti souběžnému zápisu ze dvou vláken -
    /// obojí může posílat eventy nezávisle). Otevírá se líně při prvním
    /// použití a zůstává otevřené až do konce běhu appky.
    /// </summary>
    internal static class SharedGmOutput
    {
        private const string DeviceName = "Microsoft GS Wavetable Synth";

        private static readonly object _lock = new object();
        private static OutputDevice? _device;
        private static bool _lookupDone;

        /// <summary>Vrátí sdílené zařízení (při prvním volání ho otevře); null, když se ho nepodařilo najít/otevřít.</summary>
        public static OutputDevice? TryGet()
        {
            lock (_lock)
            {
                if (_lookupDone) return _device;
                _lookupDone = true;
                try { _device = OutputDevice.GetByName(DeviceName); }
                catch { _device = null; }
                return _device;
            }
        }

        /// <summary>krok64: posílání eventů zamčené - viz komentář u třídy výš (dvě nezávislá vlákna, jedno spojení).</summary>
        public static void SendEvent(MidiEvent midiEvent)
        {
            lock (_lock)
            {
                _device?.SendEvent(midiEvent);
            }
        }
    }
}
