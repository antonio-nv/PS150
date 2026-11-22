using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PS150.Core.Common;

namespace PS150.Core.Output
{
    /// <summary>
    /// Výstup zvuku přes ALSA (Linux) - přímo, bez PulseAudio/PipeWire.
    /// Volá systémovou knihovnu libasound (P/Invoke), ta se do paměti nahraje
    /// až při prvním volání, takže na Windows tahle třída nikomu nepřekáží,
    /// dokud se nevytvoří instance (a to se tam nikdy nestane).
    ///
    /// Zařízení: "surround51:0" = 5.1 přímo na první kartu (ověřeno na
    /// ASRock ION, 6 samostatných kanálů); na RP5 s HiFiBerry DAC8x bude
    /// název karty jiný (např. "hw:sndrpihifiberry") a kanálů 8.
    ///
    /// Zápis je blokující (viz IAudioOutput.Write): volání trvá tak dlouho,
    /// než karta přijme data, což určuje tempo přehrávání.
    /// </summary>
    public sealed class AlsaOutput : IAudioOutput
    {
        // ---- ALSA konstanty (z alsa/pcm.h) ----
        private const string Lib = "libasound.so.2";
        private const int SND_PCM_STREAM_PLAYBACK = 0;
        private const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;
        private const int SND_PCM_FORMAT_S16_LE = 2;
        private const int SND_PCM_FORMAT_FLOAT_LE = 14;
        private const int EPIPE = 32;   // underrun (buffer došel) - po recover se pokračuje

        [DllImport(Lib)]
        private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);

        [DllImport(Lib)]
        private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyUs);

        [DllImport(Lib, EntryPoint = "snd_pcm_writei")]
        private static extern nint snd_pcm_writei_float(IntPtr pcm, ref float buffer, nuint frames);

        [DllImport(Lib, EntryPoint = "snd_pcm_writei")]
        private static extern nint snd_pcm_writei_short(IntPtr pcm, ref short buffer, nuint frames);

        [DllImport(Lib)]
        private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

        [DllImport(Lib)]
        private static extern int snd_pcm_drain(IntPtr pcm);

        [DllImport(Lib)]
        private static extern int snd_pcm_close(IntPtr pcm);

        [DllImport(Lib)]
        private static extern IntPtr snd_strerror(int errnum);

        // ---- stav ----
        private IntPtr _pcm = IntPtr.Zero;
        private readonly bool _useFloat;
        private short[] _shortBuffer = Array.Empty<short>();

        public string DeviceName { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        public int LatencyMs { get; }

        /// <summary>True = karta přijímá 32bitový float přímo, false = převádíme na 16 bitů.</summary>
        public bool UsesFloat => _useFloat;

        /// <summary>Kolikrát od otevření došel buffer (přerušení zvuku). U zdravého systému zůstává 0.</summary>
        public int UnderrunCount { get; private set; }

        public AlsaOutput(string deviceName = "surround51:0", int channels = 6, int sampleRate = 48000, int latencyMs = 50)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("AlsaOutput funguje jen na Linuxu.");

            if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (latencyMs < 5 || latencyMs > 2000) throw new ArgumentOutOfRangeException(nameof(latencyMs));

            DeviceName = deviceName;
            Channels = channels;
            SampleRate = sampleRate;
            LatencyMs = latencyMs;

            // Nejdřív zkusíme float (bez ztráty kvality), když ho zařízení nebere,
            // spadneme na 16 bitů. Po neúspěšném nastavení se zařízení raději
            // zavře a otevře znovu, ať v něm nezůstane napůl nastavený stav.
            string firstError;
            if (TryOpen(SND_PCM_FORMAT_FLOAT_LE, out firstError))
            {
                _useFloat = true;
                return;
            }

            if (TryOpen(SND_PCM_FORMAT_S16_LE, out string secondError))
            {
                _useFloat = false;
                return;
            }

            throw new InvalidOperationException(
                $"ALSA: zařízení '{deviceName}' se nepodařilo otevřít ({channels} kanálů, {sampleRate} Hz). " +
                $"Float: {firstError}; 16 bitů: {secondError}");
        }

        private bool TryOpen(int format, out string error)
        {
            int rc = snd_pcm_open(out IntPtr pcm, DeviceName, SND_PCM_STREAM_PLAYBACK, 0);
            if (rc < 0)
            {
                error = $"snd_pcm_open: {Describe(rc)}";
                return false;
            }

            rc = snd_pcm_set_params(
                pcm, format, SND_PCM_ACCESS_RW_INTERLEAVED,
                (uint)Channels, (uint)SampleRate,
                1,                              // soft_resample: ALSA smí přepočítat kmitočet, když ho karta nemá
                (uint)(LatencyMs * 1000));      // požadovaná celková latence v mikrosekundách

            if (rc < 0)
            {
                snd_pcm_close(pcm);
                error = $"snd_pcm_set_params: {Describe(rc)}";
                return false;
            }

            _pcm = pcm;
            error = string.Empty;
            return true;
        }

        public void Write(ReadOnlySpan<float> interleaved)
        {
            if (_pcm == IntPtr.Zero) throw new ObjectDisposedException(nameof(AlsaOutput));

            int frames = interleaved.Length / Channels;
            if (frames == 0) return;

            if (_useFloat)
            {
                WriteFloat(interleaved, frames);
            }
            else
            {
                if (_shortBuffer.Length < frames * Channels)
                    _shortBuffer = new short[frames * Channels];

                for (int i = 0; i < frames * Channels; i++)
                {
                    float v = interleaved[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    _shortBuffer[i] = (short)(v * 32767f);
                }

                WriteShort(frames);
            }
        }

        private void WriteFloat(ReadOnlySpan<float> samples, int frames)
        {
            int done = 0;
            int failures = 0;

            while (done < frames)
            {
                ReadOnlySpan<float> slice = samples.Slice(done * Channels);
                nint written = snd_pcm_writei_float(
                    _pcm, ref Unsafe.AsRef(in MemoryMarshal.GetReference(slice)), (nuint)(frames - done));

                if (written < 0)
                {
                    Recover((int)written, ref failures);
                    continue;
                }

                failures = 0;
                done += (int)written;
            }
        }

        private void WriteShort(int frames)
        {
            int done = 0;
            int failures = 0;

            while (done < frames)
            {
                Span<short> slice = _shortBuffer.AsSpan(done * Channels, (frames - done) * Channels);
                nint written = snd_pcm_writei_short(
                    _pcm, ref MemoryMarshal.GetReference(slice), (nuint)(frames - done));

                if (written < 0)
                {
                    Recover((int)written, ref failures);
                    continue;
                }

                failures = 0;
                done += (int)written;
            }
        }

        /// <summary>
        /// Zotavení po chybě zápisu. Nejčastější je underrun (-EPIPE): buffer došel,
        /// protože se data nestihla dodat. ALSA se z něj umí sama zotavit; pokud se
        /// ale chyba opakuje pořád dokola, raději vyhodíme výjimku, ať se nezacyklíme.
        /// </summary>
        private void Recover(int error, ref int failures)
        {
            failures++;
            if (error == -EPIPE) UnderrunCount++;

            int rc = snd_pcm_recover(_pcm, error, 1);
            if (rc < 0 || failures > 5)
            {
                throw new InvalidOperationException(
                    $"ALSA: zápis do '{DeviceName}' selhal: {Describe(error)} (zotavení: {Describe(rc)}).");
            }
        }

        private static string Describe(int errorCode)
        {
            try
            {
                IntPtr p = snd_strerror(errorCode);
                string? text = p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
                return $"{text ?? "?"} ({errorCode})";
            }
            catch
            {
                return $"chyba {errorCode}";
            }
        }

        public void Dispose()
        {
            if (_pcm == IntPtr.Zero) return;

            IntPtr pcm = _pcm;
            _pcm = IntPtr.Zero;

            try { snd_pcm_drain(pcm); } catch { }   // dopřehraje to, co je ještě v bufferu
            try { snd_pcm_close(pcm); } catch { }
        }
    }
}
