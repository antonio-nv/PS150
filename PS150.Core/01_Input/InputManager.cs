using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

namespace PS150.Core.Input
{
    // ====================================================================
    // DATOVÉ STRUKTURY PRO PROPOJENÍ VSTUPU SE SYNTÉZOU
    // ====================================================================

    public enum InputEventType
    {
        NoteOn,
        NoteOff,
        ControlChange
    }

   
    public class ConsoleInputEvent
    {
        public InputEventType Type { get; set; }
        public Note Note { get; set; }
        public float Velocity { get; set; }        // Dynamika (0.0 až 1.0)
        public int Channel { get; set; }           // MIDI kanál
        public string Source { get; set; } = "";   // Zdroj ("Live_Piano" / "Midi_File")
        public int Controller { get; set; }        // NOVÉ: číslo ovladače u ControlChange (64 = sustain pedál)
        public int ControllerValue { get; set; }   // NOVÉ: hodnota ovladače 0-127
        public int PortIndex { get; set; } = -1;   // NOVÉ: které MIDI-IN rozhraní událost poslalo (-1 = soubor)
    }

    // ====================================================================
    // PŘIJÍMAČ UDÁLOSTÍ (ŽIVÉ PIANO + SOUBOR)
    // ====================================================================

    public class InputManager : IDisposable
    {
        private CancellationTokenSource? _filePlaybackCts;

        // NOVÉ: všechna MIDI-IN rozhraní (hot-plug). Každé se po připojení samo otevře,
        // po odpojení zavře a pustí držené noty. Rozhraní se evidují v pořadí, v jakém
        // se poprvé objevila, a zůstávají v seznamu i po odpojení (řádek zešedne).
        private readonly List<MidiPortInfo> _ports = new List<MidiPortInfo>();
        private readonly object _portLock = new object();
        private readonly object _refreshLock = new object();
        private Timer? _hotPlugTimer;

        public event Action<ConsoleInputEvent>? OnInputEvent;

        /// <summary>True, pokud je právě připojené aspoň jedno fyzické MIDI-IN zařízení.</summary>
        public bool IsLiveDeviceConnected
        {
            get
            {
                lock (_portLock) return _ports.Exists(p => p.Connected);
            }
        }

        /// <summary>Obraz všech dosud viděných rozhraní (i odpojených) pro formulář.</summary>
        public MidiPortSnapshot[] GetPorts()
        {
            lock (_portLock)
            {
                var result = new MidiPortSnapshot[_ports.Count];
                for (int i = 0; i < _ports.Count; i++)
                {
                    var p = _ports[i];
                    var recent = new string[p.Log.Count];
                    for (int k = 0; k < recent.Length; k++) recent[k] = p.Log[k].Text;
                    result[i] = new MidiPortSnapshot(p.Index, p.Name, p.Connected, p.EventCount, recent);
                }
                return result;
            }
        }

        /// <summary>
        /// Spustí sledování všech MIDI-IN rozhraní: první průchod hned (ať je
        /// IsLiveDeviceConnected správně už při startu), další každou sekundu (hot-plug).
        /// </summary>
        public void StartMidiInputs()
        {
            RefreshPorts();
            _hotPlugTimer ??= new Timer(_ => RefreshPorts(), null, 1000, 1000);
        }

        /// <summary>Zpětná kompatibilita: filtr podle jména se už nepoužívá, otevírají se všechna rozhraní.</summary>
        public void StartLiveDevice(string deviceNameSearch = "")
        {
            StartMidiInputs();
        }

        private void RefreshPorts()
        {
            if (!Monitor.TryEnter(_refreshLock)) return;

            try
            {
                // Názvy zjišťujeme přes NAudio (jen se zeptá, nic neotevírá) - DryWetMidi by
                // při výčtu zkoušel otevřít i rozhraní, které už držíme otevřené.
                int count = NAudio.Midi.MidiIn.NumberOfDevices;
                var present = new HashSet<string>();
                var occurrences = new Dictionary<string, int>();

                for (int index = 0; index < count; index++)
                {
                    string name;
                    try { name = NAudio.Midi.MidiIn.DeviceInfo(index).ProductName; }
                    catch { continue; }

                    occurrences.TryGetValue(name, out int n);
                    occurrences[name] = n + 1;
                    string key = name + "#" + n;
                    present.Add(key);

                    MidiPortInfo? port;
                    lock (_portLock) port = _ports.Find(p => p.Key == key);
                    if (port != null && port.Connected) continue;

                    InputDevice? device = null;
                    try
                    {
                        device = InputDevice.GetByIndex(index);
                        device.EventReceived += OnMidiEventReceived;
                        device.StartEventsListening();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Core.Input] '{name}' nejde otevřít: {ex.Message}");
                        try { device?.Dispose(); } catch { }
                        continue;   // zkusí se znovu při dalším průchodu
                    }

                    lock (_portLock)
                    {
                        if (port == null)
                        {
                            port = new MidiPortInfo(_ports.Count, name, key);
                            _ports.Add(port);
                        }
                        port.Device = device;
                        port.Connected = true;
                    }

                    System.Diagnostics.Debug.WriteLine($"[Core.Input] Připojeno: {name}");
                }

                // Odpojená rozhraní.
                List<MidiPortInfo> gone;
                lock (_portLock) gone = _ports.FindAll(p => p.Connected && !present.Contains(p.Key));
                foreach (var port in gone) DisconnectPort(port);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Core.Input] Chyba při hledání MIDI: {ex.Message}");
            }
            finally
            {
                Monitor.Exit(_refreshLock);
            }
        }

        private void DisconnectPort(MidiPortInfo port)
        {
            InputDevice? device;
            int[] held;

            lock (_portLock)
            {
                device = port.Device;
                port.Device = null;
                port.Connected = false;
                held = new int[port.Held.Count];
                port.Held.CopyTo(held);
                port.Held.Clear();
            }

            if (device != null)
            {
                try { device.EventReceived -= OnMidiEventReceived; } catch { }
                try { device.StopEventsListening(); } catch { }
                try { device.Dispose(); } catch { }
            }

            // Vytažený kabel by jinak nechal noty znít navždy (i sešlápnutý pedál).
            OnInputEvent?.Invoke(new ConsoleInputEvent
            {
                Type = InputEventType.ControlChange,
                Controller = 64,
                ControllerValue = 0,
                Source = "Live_Piano",
                PortIndex = port.Index
            });

            foreach (int code in held)
            {
                Emit(InputEventType.NoteOff, code % 128, 0.0f, code / 128, "Live_Piano", port.Index);
            }
        }

        private void OnMidiEventReceived(object? sender, MidiEventReceivedEventArgs e)
        {
            MidiPortInfo? port;
            lock (_portLock) port = _ports.Find(p => ReferenceEquals(p.Device, sender));
            if (port == null) return;

            var ev = e.Event;
            string? key = null;
            string? text = null;

            if (ev is NoteOnEvent noteOn)
            {
                int note = noteOn.NoteNumber;
                int ch = noteOn.Channel;
                bool on = noteOn.Velocity > 0;
                lock (_portLock)
                {
                    if (on) port.Held.Add(ch * 128 + note); else port.Held.Remove(ch * 128 + note);
                }
                text = on ? $"{ch + 1}:{NoteLabel(ch, note)} {(int)noteOn.Velocity}" : $"{ch + 1}:{NoteLabel(ch, note)} -";
                Log(port, null, text);
                Emit(on ? InputEventType.NoteOn : InputEventType.NoteOff, note, noteOn.Velocity / 127.0f, ch, "Live_Piano", port.Index);
                return;
            }

            if (ev is NoteOffEvent noteOff)
            {
                int note = noteOff.NoteNumber;
                int ch = noteOff.Channel;
                lock (_portLock) port.Held.Remove(ch * 128 + note);
                Log(port, null, $"{ch + 1}:{NoteLabel(ch, note)} -");
                Emit(InputEventType.NoteOff, note, 0.0f, ch, "Live_Piano", port.Index);
                return;
            }

            // Ostatní zprávy zatím jen do záznamu. Spojité (pedál, kolečko, aftertouch)
            // se slučují, ať jedno otočení kolečka nezaplaví celý řádek.
            if (ev is ControlChangeEvent cc)
            {
                // NOVÉ: pedál sustain (CC64) se předává dál (kanál 10 = bicí, tam pedál neřešíme).
                if ((int)cc.ControlNumber == 64 && (int)cc.Channel != 9)
                {
                    OnInputEvent?.Invoke(new ConsoleInputEvent
                    {
                        Type = InputEventType.ControlChange,
                        Channel = (int)cc.Channel,
                        Controller = 64,
                        ControllerValue = (int)cc.ControlValue,
                        Source = "Live_Piano",
                        PortIndex = port.Index
                    });
                }

                key = $"CC{(int)cc.Channel}:{(int)cc.ControlNumber}";
                text = $"{(int)cc.Channel + 1}:CC{(int)cc.ControlNumber}={(int)cc.ControlValue}";
            }
            else if (ev is PitchBendEvent pb)
            {
                key = $"PB{(int)pb.Channel}";
                int bend = pb.PitchValue - 8192;
                text = $"{(int)pb.Channel + 1}:PB{(bend >= 0 ? "+" : "")}{bend}";
            }
            else if (ev is ProgramChangeEvent pc)
            {
                text = $"{(int)pc.Channel + 1}:PG{(int)pc.ProgramNumber}";
            }
            else if (ev is ChannelAftertouchEvent cat)
            {
                key = $"AT{(int)cat.Channel}";
                text = $"{(int)cat.Channel + 1}:AT{(int)cat.AftertouchValue}";
            }
            else if (ev is NoteAftertouchEvent nat)
            {
                key = $"NA{(int)nat.Channel}:{(int)nat.NoteNumber}";
                text = $"{(int)nat.Channel + 1}:{NoteLabel((int)nat.Channel, (int)nat.NoteNumber)}AT{(int)nat.AftertouchValue}";
            }

            // Časování (clock) a Active Sensing se nezobrazují, to by řádek zahltilo.
            if (text != null) Log(port, key, text);
        }

        private void Log(MidiPortInfo port, string? key, string text)
        {
            lock (_portLock)
            {
                port.EventCount++;

                int last = port.Log.Count - 1;
                if (key != null && last >= 0 && port.Log[last].Key == key)
                {
                    port.Log[last] = (key, text);
                    return;
                }

                port.Log.Add((key ?? "", text));
                if (port.Log.Count > MidiPortInfo.LogCapacity) port.Log.RemoveAt(0);
            }
        }

        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>Název noty: 60 = C4. Na kanálu 10 (index 9, bicí) se píše číslo noty, protože určuje nástroj (36 = basák...).</summary>
        private static string NoteLabel(int channel, int note) =>
            channel == 9 ? note.ToString() : NoteNames[note % 12] + (note / 12 - 1);

        public async Task PlayMidiFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) return;

            StopFilePlayback();
            _filePlaybackCts = new CancellationTokenSource();
            var token = _filePlaybackCts.Token;

            await Task.Run(() =>
            {
                // NOVÉ: tolerantní čtení - některé (hlavně herní) .mid soubory mají
                // v řídicí události hodnotu mimo rozsah 0-127 (např. 128). DryWetMidi
                // by jinak celý soubor odmítl s chybou "128 is invalid value ...";
                // SnapToLimits hodnotu oseká na nejbližší platnou (127), jak to dělají
                // jiné přehrávače.
                var midiFile = MidiFile.Read(filePath, new ReadingSettings
                {
                    InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits
                });

                using var playback = new Playback(midiFile.GetTimedEvents(), midiFile.GetTempoMap());

                playback.EventPlayed += (sender, e) =>
                {
                    if (e.Event is NoteOnEvent noteOn)
                    {
                        var type = noteOn.Velocity > 0 ? InputEventType.NoteOn : InputEventType.NoteOff;
                        Emit(type, noteOn.NoteNumber, noteOn.Velocity / 127.0f, noteOn.Channel, "Midi_File");
                    }
                    else if (e.Event is NoteOffEvent noteOff)
                    {
                        Emit(InputEventType.NoteOff, noteOff.NoteNumber, 0.0f, noteOff.Channel, "Midi_File");
                    }
                };

                playback.Start();

                while (playback.IsRunning)
                {
                    if (token.IsCancellationRequested)
                    {
                        playback.Stop();
                        break;
                    }
                    Thread.Sleep(10);
                }
            }, token);
        }

        public void StopFilePlayback()
        {
            _filePlaybackCts?.Cancel();
            _filePlaybackCts?.Dispose();
            _filePlaybackCts = null;
        }

        private void Emit(InputEventType type, int noteNumber, float velocity, int channel, string source, int portIndex = -1)
        {
            var note = new Note(noteNumber);

            OnInputEvent?.Invoke(new ConsoleInputEvent
            {
                Type = type,
                Note = note,
                Velocity = velocity,
                Channel = channel,
                Source = source,
                PortIndex = portIndex
            });
        }

        public void Dispose()
        {
            StopFilePlayback();

            _hotPlugTimer?.Dispose();
            _hotPlugTimer = null;

            List<MidiPortInfo> open;
            lock (_portLock) open = _ports.FindAll(p => p.Connected);
            foreach (var port in open) DisconnectPort(port);
        }
    }
}