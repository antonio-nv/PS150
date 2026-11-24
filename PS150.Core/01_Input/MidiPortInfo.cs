using System;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Multimedia;

namespace PS150.Core.Input
{
    /// <summary>
    /// NOVÉ: jedno MIDI-IN rozhraní (USB převodník, klavír, bicí...). Drží si otevřené
    /// zařízení, stav připojeno/odpojeno, poslední události (pro řádek formuláře)
    /// a držené noty (aby se při odpojení kabelu daly pustit).
    /// Vnitřní stav patří InputManageru; UI čte jen neměnný <see cref="MidiPortSnapshot"/>.
    /// </summary>
    internal sealed class MidiPortInfo
    {
        public const int LogCapacity = 40;

        public int Index { get; }
        public string Name { get; }
        public string Key { get; }

        public InputDevice? Device { get; set; }
        public bool Connected { get; set; }
        public long EventCount { get; set; }

        /// <summary>Poslední události: klíč (pro slučování spojitých ovladačů) a text.</summary>
        public List<(string Key, string Text)> Log { get; } = new List<(string, string)>();

        /// <summary>Držené noty: kanál * 128 + nota.</summary>
        public HashSet<int> Held { get; } = new HashSet<int>();

        public MidiPortInfo(int index, string name, string key)
        {
            Index = index;
            Name = name;
            Key = key;
        }
    }

    /// <summary>Neměnný obraz rozhraní pro vykreslení formuláře.</summary>
    public sealed class MidiPortSnapshot
    {
        public int Index { get; }
        public string Name { get; }
        public bool Connected { get; }
        public long EventCount { get; }

        /// <summary>Poslední události, nejstarší první, nejnovější poslední (vpravo).</summary>
        public string[] Recent { get; }

        public MidiPortSnapshot(int index, string name, bool connected, long eventCount, string[] recent)
        {
            Index = index;
            Name = name;
            Connected = connected;
            EventCount = eventCount;
            Recent = recent;
        }
    }
}
