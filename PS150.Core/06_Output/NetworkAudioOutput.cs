using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using PS150.Core.Common;
using PS150.Core.Network;

namespace PS150.Core.Output
{
    /// <summary>
    /// Výstup zvuku po síti (UDP) do externí zvukovky - protějšek AlsaOutput.
    /// Místo do zvukové karty se zvuk balí do paketů (viz AudioPacket) a
    /// posílá na adresu příjemce.
    ///
    /// Tempo: zvuková karta počítač "táhne" vlastními hodinami, tady žádná
    /// není, takže si tempo hlídáme sami podle systémových hodin: každý paket
    /// se odešle v čase, kdy má začít znít. Write() proto trvá tak dlouho,
    /// jako přehrání předaných vzorků (stejně jako u AlsaOutput). Drobný
    /// rozdíl mezi našimi hodinami a hodinami karty příjemce vyrovnává jeho
    /// jitter buffer.
    ///
    /// Pokud zápis do sítě selže (síť chvíli není), chyba se jen započítá
    /// (SendErrors); přehrávání se kvůli tomu nezastavuje.
    /// </summary>
    public sealed class NetworkAudioOutput : IAudioOutput
    {
        // Windows má výchozí rozlišení časovače 15,6 ms, takže Thread.Sleep(1) trvá
        // až 15 ms a pakety by odcházely v dávkách (příjemci to zhoršuje kolísání).
        // timeBeginPeriod(1) ho zjemní na 1 ms po dobu, kdy výstup existuje.
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint milliseconds);

        private bool _timerPeriodSet;

        private const double MaxLateMs = 50.0;    // když jsme po termínu víc, než tohle, tempo se přenastaví (žádné dohánění dávkou)

        private readonly Socket _socket;
        private readonly AudioSampleFormat _format;
        private readonly int _packetFrames;
        private readonly float[] _pending;
        private readonly byte[] _packet;
        private readonly uint _streamId;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private int _pendingCount;
        private uint _sequence;
        private long _framesSent;
        private long _baseFrames;
        private double _baseMs;
        private bool _started;
        private bool _disposed;

        public int SampleRate { get; }
        public int Channels { get; }

        public long PacketsSent { get; private set; }
        public long SendErrors { get; private set; }

        public NetworkAudioOutput(
            string host, int port, int sampleRate, int channels,
            int packetFrames = NetworkDefaults.PacketFrames,
            AudioSampleFormat format = AudioSampleFormat.Float32)
        {
            if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (packetFrames < 16 || packetFrames > 1024) throw new ArgumentOutOfRangeException(nameof(packetFrames));

            SampleRate = sampleRate;
            Channels = channels;
            _packetFrames = packetFrames;
            _format = format;
            _pending = new float[packetFrames * channels];
            _packet = new byte[AudioPacket.EncodedSize(packetFrames, channels, format)];
            _streamId = (uint)Random.Shared.Next();

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Connect(new IPEndPoint(IPAddress.Parse(host), port));   // u UDP jen nastaví cíl, nic se nenavazuje

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    TimeBeginPeriod(1);
                    _timerPeriodSet = true;
                }
                catch (Exception) { /* bez zjemnění to funguje taky, jen s větším kolísáním */ }
            }
        }

        public void Write(ReadOnlySpan<float> interleaved)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NetworkAudioOutput));

            int offset = 0;
            while (offset < interleaved.Length)
            {
                int take = Math.Min(_pending.Length - _pendingCount, interleaved.Length - offset);
                interleaved.Slice(offset, take).CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += take;
                offset += take;

                if (_pendingCount == _pending.Length)
                {
                    SendPacket();
                    _pendingCount = 0;
                }
            }
        }

        private void SendPacket()
        {
            double now = _clock.Elapsed.TotalMilliseconds;

            if (!_started)
            {
                _started = true;
                _baseFrames = _framesSent;
                _baseMs = now;
            }

            double due = _baseMs + (_framesSent - _baseFrames) * 1000.0 / SampleRate;

            if (now - due > MaxLateMs)
            {
                // zdržení (pauza, přeskočení v souboru...): tempo začínáme znovu od teď
                _baseFrames = _framesSent;
                _baseMs = now;
                due = now;
            }

            // Čekání do termínu: hrubě spaním, poslední milisekundu přesně.
            while (true)
            {
                double remaining = due - _clock.Elapsed.TotalMilliseconds;
                if (remaining <= 0.0) break;
                if (remaining > 1.5) Thread.Sleep(1); else Thread.SpinWait(50);
            }

            try
            {
                int length = AudioPacket.Encode(
                    _packet, _streamId, _sequence, _framesSent, SampleRate, Channels, _format, _pending);
                _socket.Send(_packet, 0, length, SocketFlags.None);
                PacketsSent++;
            }
            catch (SocketException)
            {
                SendErrors++;
            }

            _sequence++;
            _framesSent += _packetFrames;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _socket.Close(); } catch { }

            if (_timerPeriodSet)
            {
                _timerPeriodSet = false;
                try { TimeEndPeriod(1); } catch (Exception) { }
            }
        }
    }
}
