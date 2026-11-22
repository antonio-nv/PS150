using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PS150.Core.Network;

namespace PS150.Core.Input
{
    /// <summary>
    /// Příjem zvuku po síti (UDP) - strana externí zvukovky (ASRock, RP5).
    /// Poslouchá na jednom portu a rozlišuje dva druhy datagramů:
    ///  - zvukový paket ('P','S') -> předá ho jitter bufferu,
    ///  - dotaz "jsi tam?" ('P','Q') -> odpoví svými parametry (ReceiverInfo).
    /// Cokoliv jiného se tiše zahodí (v síti se vždycky najde cizí provoz).
    ///
    /// Čtení ze zvukového bufferu a výstup na kartu si řeší hostitel
    /// (AudioJitterBuffer.Read), tahle třída se stará jen o síť.
    /// </summary>
    public sealed class NetworkAudioInput : IDisposable
    {
        private readonly int _port;
        private readonly AudioJitterBuffer _jitter;
        private readonly ReceiverInfo _info;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private Socket? _socket;
        private Thread? _thread;
        private volatile bool _quit;
        private long _lastAudioMs = long.MinValue / 2;

        public long AudioPackets { get; private set; }
        public long ProbesAnswered { get; private set; }
        public long BadDatagrams { get; private set; }

        /// <summary>Jak dávno (ms) přišel poslední zvukový paket; velké číslo = nikdo nevysílá.</summary>
        public double LastAudioAgeMs => _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastAudioMs);

        public NetworkAudioInput(int port, AudioJitterBuffer jitter, ReceiverInfo info)
        {
            _port = port;
            _jitter = jitter;
            _info = info;
        }

        public void Start()
        {
            if (_thread != null) return;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            socket.ReceiveBufferSize = 1 << 20;     // 1 MB: při krátkém zaváhání vlákna se pakety neztratí
            socket.ReceiveTimeout = 200;            // ať se vlákno dá rychle ukončit
            _socket = socket;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "NetworkAudioInput",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        private void Loop()
        {
            var socket = _socket!;
            var buffer = new byte[2048];
            var reply = new byte[AudioDiscovery.ReplySize];

            while (!_quit)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int length;

                try
                {
                    length = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException ex)
                {
                    if (_quit) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut) Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (length < 2 || buffer[0] != (byte)'P')
                {
                    BadDatagrams++;
                    continue;
                }

                var data = new ReadOnlySpan<byte>(buffer, 0, length);

                if (buffer[1] == (byte)'S')
                {
                    if (AudioPacket.TryDecode(data, out AudioPacket? packet) && packet != null)
                    {
                        _jitter.Push(packet);
                        AudioPackets++;
                        Interlocked.Exchange(ref _lastAudioMs, _clock.ElapsedMilliseconds);
                    }
                    else
                    {
                        BadDatagrams++;
                    }
                }
                else if (buffer[1] == (byte)'Q')
                {
                    if (AudioDiscovery.TryDecodeProbe(data, out uint nonce))
                    {
                        int n = AudioDiscovery.EncodeReply(reply, nonce, _info);
                        try
                        {
                            socket.SendTo(reply, 0, n, SocketFlags.None, remote);
                            ProbesAnswered++;
                        }
                        catch (SocketException) { }
                    }
                    else
                    {
                        BadDatagrams++;
                    }
                }
                else
                {
                    BadDatagrams++;
                }
            }
        }

        public void Dispose()
        {
            _quit = true;
            try { _socket?.Close(); } catch { }
            _thread?.Join(500);
        }
    }
}
