using System;
using System.Collections.Generic;
using PS150.Core.ToneEngine;
using NAudio.Wave;

namespace PS150.Core.Output

{
    public class AudioEngine : IWaveProvider, IDisposable
    {
        private WaveOutEvent? _waveOut;
        private readonly PS150.Core.ToneEngine.ToneEngine _synth;

        // Špičková úroveň od posledního čtení (pro VU metr). Stejný princip
        // jako MaxLeftPeak/MaxRightPeak v AudioPlayer.cs.
        private float _peakSinceLastRead;

        public WaveFormat WaveFormat { get; }

        public AudioEngine(PS150.Core.ToneEngine.ToneEngine synth, int sampleRate = 44100)
        {
            _synth = synth;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1); // 44.1 kHz, Mono, 32-bit Float
        }

        /// <summary>
        /// Spustí audio výstup na zvukovou kartu.
        /// </summary>
        public void Start()
        {
            if (_waveOut != null) return;

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100, // Nízká latence (50 ms) pro živé hraní bez zpoždění
                NumberOfBuffers = 4
            };

            _waveOut.Init(this);
            _waveOut.Play();
        }

        /// <summary>
        /// Zastaví audio výstup.
        /// </summary>
        public void Stop()
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
        }

        /// <summary>
        /// Callback od NAudio – plníme zvukový buffer vzorky z našeho PolySynthu.
        /// ZMĚNA: syntezátor je jediný mono, tři pásma jsou pryč. Dělení do pásem
        /// pro dvou/třípásmovou reproduktorovou soustavu přijde jako výhybka až
        /// na výstupu.
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int sampleCount = count / 4;
            var waveBuffer = new WaveBuffer(buffer);

            for (int i = 0; i < sampleCount; i++)
            {
                float sample = (float)_synth.GenerateNextSample();

                TrackPeak(ref _peakSinceLastRead, sample);

                waveBuffer.FloatBuffer[offset / 4 + i] = sample;
            }

            return count;
        }

        private static void TrackPeak(ref float peakStore, double sample)
        {
            float abs = (float)Math.Abs(sample);
            if (abs > peakStore) peakStore = abs;
        }

        /// <summary>
        /// Vrátí nejvyšší absolutní úroveň vzorku od posledního volání a vynuluje počítadlo.
        /// Určeno pro VU metr (stejný princip jako AudioPlayer.ReadPeakLevels() u MP3 přehrávače).
        /// </summary>
        public float ReadPeak()
        {
            float peak = _peakSinceLastRead;
            _peakSinceLastRead = 0f;
            return peak;
        }

        /// <summary>
        /// True, pokud poslední vygenerovaný vzorek přesáhl
        /// rozsah -1.0..1.0 (tedy došlo k oříznutí). Průchozí hodnota ze ToneEngine.
        /// </summary>
        public bool ClipDetected => _synth.ClipDetected;

        // --- Průchozí ovládání rejstříků (pro VGA panel / budoucí UI) ---

        /// <summary>Přepne rejstřík ON/OFF podle čísla. Vrací nový stav (true = ON).</summary>
        public bool ToggleRegister(int number) => _synth.ToggleRegister(number);

        public bool IsRegisterActive(int number) => _synth.IsRegisterActive(number);

        public IReadOnlyCollection<int> ActiveRegisters => _synth.ActiveRegisters;

        public void Dispose()
        {
            Stop();
        }
    }
}
