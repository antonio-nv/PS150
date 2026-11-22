using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PS150.Core.Network
{
    /// <summary>
    /// Hlídá, jestli je na síti externí zvukovka (ASRock / RP5): jednou za
    /// sekundu jí pošle dotaz a čeká na odpověď (viz AudioDiscovery).
    ///
    ///  - Po NetworkDefaults.RepliesToActivate odpovědích za sebou se
    ///    příjemce prohlásí za dostupného (událost Changed s jeho parametry).
    ///  - Když odpověď nepřijde NetworkDefaults.TimeoutMs, příjemce se
    ///    prohlásí za nedostupného (Changed s null).
    ///
    /// Událost přichází z vlastního vlákna - kdo na ni reaguje v okně, musí
    /// přejít na vlákno rozhraní (Dispatcher).
    /// </summary>
    public sealed class AudioReceiverWatcher : IDisposable
    {
        private readonly IPEndPoint _endpoint;
        private readonly UdpClient _udp;
        private Thread? _thread;
        private volatile bool _quit;
        private volatile ReceiverInfo? _current;

        public event Action<ReceiverInfo?>? Changed;

        /// <summary>Adresa a port příjemce.</summary>
        public IPEndPoint Endpoint => _endpoint;

        /// <summary>Parametry příjemce, je-li dostupný; jinak null.</summary>
        public ReceiverInfo? Current => _current;

        public AudioReceiverWatcher(string address, int port)
        {
            _endpoint = new IPEndPoint(IPAddress.Parse(address), port);
            _udp = new UdpClient(0);     // 0 = systém vybere volný odchozí port
        }

        public void Start()
        {
            if (_thread != null) return;

            _thread = new Thread(Loop) { IsBackground = true, Name = "AudioReceiverWatcher" };
            _thread.Start();
        }

        private void Loop()
        {
            var clock = Stopwatch.StartNew();
            var probe = new byte[AudioDiscovery.ProbeSize];
            uint nonce = (uint)Random.Shared.Next();

            long nextProbeMs = 0;
            long lastReplyMs = long.MinValue / 2;
            int consecutive = 0;

            while (!_quit)
            {
                long now = clock.ElapsedMilliseconds;

                if (now >= nextProbeMs)
                {
                    nextProbeMs = now + NetworkDefaults.ProbeIntervalMs;
                    try
                    {
                        AudioDiscovery.EncodeProbe(probe, nonce);
                        _udp.Send(probe, probe.Length, _endpoint);
                    }
                    catch (SocketException) { /* síť zrovna není - zkusíme za sekundu znovu */ }
                    catch (ObjectDisposedException) { break; }
                }

                try
                {
                    // Čekáme nejvýš 100 ms, ať se vlákno dá rychle ukončit a včas se pošle další dotaz.
                    if (_udp.Client.Poll(100_000, SelectMode.SelectRead))
                    {
                        while (_udp.Available > 0)
                        {
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            byte[] data = _udp.Receive(ref from);

                            if (!from.Address.Equals(_endpoint.Address)) continue;
                            if (!AudioDiscovery.TryDecodeReply(data, out uint replyNonce, out ReceiverInfo? info) || info == null) continue;
                            if (replyNonce != nonce) continue;

                            long replyAt = clock.ElapsedMilliseconds;
                            if (replyAt - lastReplyMs > NetworkDefaults.ProbeIntervalMs * 2) consecutive = 0;
                            consecutive++;
                            lastReplyMs = replyAt;

                            if (_current == null && consecutive >= NetworkDefaults.RepliesToActivate)
                            {
                                _current = info;
                                Changed?.Invoke(info);
                            }
                            else if (_current != null)
                            {
                                _current = info;   // parametry se mohly změnit, ale výstup nepřepínáme
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (_current != null && clock.ElapsedMilliseconds - lastReplyMs > NetworkDefaults.TimeoutMs)
                {
                    _current = null;
                    consecutive = 0;
                    Changed?.Invoke(null);
                }
            }
        }

        public void Dispose()
        {
            _quit = true;
            try { _udp.Close(); } catch { }
            _thread?.Join(500);
        }
    }
}
