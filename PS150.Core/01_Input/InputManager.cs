using System;
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
    }

    // ====================================================================
    // PŘIJÍMAČ UDÁLOSTÍ (ŽIVÉ PIANO + SOUBOR)
    // ====================================================================

    public class InputManager : IDisposable
    {
        private InputDevice? _liveMidiDevice;
        private CancellationTokenSource? _filePlaybackCts;

        public event Action<ConsoleInputEvent>? OnInputEvent;

        /// <summary>True, pokud se při StartLiveDevice podařilo najít a připojit fyzické MIDI-IN zařízení (např. USB klaviatura).</summary>
        public bool IsLiveDeviceConnected => _liveMidiDevice != null;

        public void StartLiveDevice(string deviceNameSearch = "USB MIDI")
        {
            try
            {
                var devices = InputDevice.GetAll();
                _liveMidiDevice = devices.FirstOrDefault(d => d.Name.Contains(deviceNameSearch, StringComparison.OrdinalIgnoreCase));

                if (_liveMidiDevice == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Core.Input] Zařízení '{deviceNameSearch}' nenalezeno.");
                    return;
                }

                _liveMidiDevice.EventReceived += OnMidiEventReceived;
                _liveMidiDevice.StartEventsListening();
                System.Diagnostics.Debug.WriteLine($"[Core.Input] Připojeno k: {_liveMidiDevice.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Core.Input] Chyba při startu MIDI: {ex.Message}");
            }
        }

        private void OnMidiEventReceived(object? sender, MidiEventReceivedEventArgs e)
        {
            if (e.Event is NoteOnEvent noteOn)
            {
                var type = noteOn.Velocity > 0 ? InputEventType.NoteOn : InputEventType.NoteOff;
                Emit(type, noteOn.NoteNumber, noteOn.Velocity / 127.0f, noteOn.Channel, "Live_Piano");
            }
            else if (e.Event is NoteOffEvent noteOff)
            {
                Emit(InputEventType.NoteOff, noteOff.NoteNumber, 0.0f, noteOff.Channel, "Live_Piano");
            }
        }

        public async Task PlayMidiFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) return;

            StopFilePlayback();
            _filePlaybackCts = new CancellationTokenSource();
            var token = _filePlaybackCts.Token;

            await Task.Run(() =>
            {
                var midiFile = MidiFile.Read(filePath);

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

        private void Emit(InputEventType type, int noteNumber, float velocity, int channel, string source)
        {
            var note = new Note(noteNumber);

            OnInputEvent?.Invoke(new ConsoleInputEvent
            {
                Type = type,
                Note = note,
                Velocity = velocity,
                Channel = channel,
                Source = source
            });
        }

        public void Dispose()
        {
            StopFilePlayback();
            if (_liveMidiDevice != null)
            {
                _liveMidiDevice.EventReceived -= OnMidiEventReceived;
                _liveMidiDevice.StopEventsListening();
                _liveMidiDevice.Dispose();
            }
        }
    }
}