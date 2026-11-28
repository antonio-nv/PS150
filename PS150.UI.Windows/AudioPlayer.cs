using System;
using System.Net;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PS150.Core.Network;
using PS150.Core.Output;
using PS150.Core.Mixers;   // krok88: StereoMonoChannelSampleProvider, MonoChannel
using PS150.Core.Equalizers;   // krok89: GraphicEqualizer32Band

namespace PS150.UI.Windows
{
    // krok105: implementuje IStereoDspSource (společně s GmPianoMidiPlayer) -
    // FileBrowserWindow díky tomu může ovládat .mp3 i .mid stejným kódem,
    // viz FileBrowserWindow.ActiveStereoSource.
    public class AudioPlayer : IDisposable, IStereoDspSource
    {
        // ZMĚNA: místo přímo WasapiOut je výstup "trasa" (IAudioRoute) - buď
        // zvuková karta počítače (WASAPI), nebo síť (externí zvukovka, viz
        // SetNetworkTarget). Zbytek přehrávače o rozdílu nic neví.
        private IAudioRoute? _route;
        private bool _routeIsNetwork;

        private AudioFileReader? _audioFile;
        private MeteringSampleProvider? _meteringProvider;

        // krok88: zkušební zapojení - levý a pravý kanál každý přes svůj vlastní,
        // nezávislý MonoChannel (viz BuildOutput). Null, pokud zdroj není stereo
        // (StereoMonoChannelSampleProvider by jinak stejně odmítl).
        private StereoMonoChannelSampleProvider? _stereoChannelStrip;

        /// <summary>krok88: kanálový pás levého kanálu (Gain/HPF/Gate/Comp/EQ) - null, pokud právě přehrávaný soubor není stereo.</summary>
        public MonoChannel? LeftChannelStrip => _stereoChannelStrip?.Left;

        /// <summary>krok88: kanálový pás pravého kanálu (Gain/HPF/Gate/Comp/EQ) - null, pokud právě přehrávaný soubor není stereo.</summary>
        public MonoChannel? RightChannelStrip => _stereoChannelStrip?.Right;

        /// <summary>krok89: skutečný 32pásmový ekvalizér levého kanálu - null, pokud právě přehrávaný soubor není stereo.</summary>
        public GraphicEqualizer32Band? LeftEq32 => _stereoChannelStrip?.LeftEq32;

        /// <summary>krok89: skutečný 32pásmový ekvalizér pravého kanálu - null, pokud právě přehrávaný soubor není stereo.</summary>
        public GraphicEqualizer32Band? RightEq32 => _stereoChannelStrip?.RightEq32;

        /// <summary>krok96: skutečný spektrální analyzér levého kanálu (za ekvalizérem) - null, pokud právě přehrávaný soubor není stereo.</summary>
        public SpectrumAnalyzer32Band? LeftAnalyzer32 => _stereoChannelStrip?.LeftAnalyzer32;

        /// <summary>krok96: skutečný spektrální analyzér pravého kanálu (za ekvalizérem) - null, pokud právě přehrávaný soubor není stereo.</summary>
        public SpectrumAnalyzer32Band? RightAnalyzer32 => _stereoChannelStrip?.RightAnalyzer32;

        /// <summary>
        /// krok101: živé místo odběru obou analyzérů - false (výchozí) = za
        /// ekvalizérem, true = před ekvalizérem (viz StereoMonoChannelSampleProvider.
        /// AnalyzerTapPreEq). Pokud právě nehraje stereo soubor, nastavení se jen
        /// tiše neuplatní (get vrátí false) - FileBrowserWindow si ale svoje
        /// přání pamatuje ve vlastním poli a po každém Load() ho sem znovu pošle.
        /// </summary>
        public bool AnalyzerTapPreEq
        {
            get => _stereoChannelStrip?.AnalyzerTapPreEq ?? false;
            set { if (_stereoChannelStrip != null) _stereoChannelStrip.AnalyzerTapPreEq = value; }
        }

        // NOVÉ: ladění změnou rychlosti (varispeed). 1.0 = beze změny.
        // Hodnota se pamatuje i mezi soubory - nastaví se jednou (SetSpeedFactor)
        // a platí pro každý další Load(), dokud ji někdo nezmění.
        private double _speedFactor = 1.0;

        // NOVÉ: síťový výstup. Cíl se nastavuje zvenčí (SetNetworkTarget);
        // _networkOutput je jeden proud pro všechny skladby po sobě, dokud je
        // příjemce dostupný (příjemce tak mezi skladbami nic nerestartuje).
        private IPEndPoint? _networkEndpoint;
        private ReceiverInfo? _networkInfo;
        private NetworkAudioOutput? _networkOutput;

        public event Action? PlaybackEnded;

        public float MaxLeftPeak { get; private set; }
        public float MaxRightPeak { get; private set; }

        /// <summary>Aktuální poměr rychlosti přehrávání (1.0 = původní).</summary>
        public double SpeedFactor => _speedFactor;

        public bool IsPlaying => _route?.State == PlaybackState.Playing;

        /// <summary>True, pokud zvuk právě jde po síti do externí zvukovky (ne do zvukovky počítače).</summary>
        public bool UsesNetwork => _routeIsNetwork;

        /// <summary>
        /// Zpoždění zvuku na straně externí zvukovky v ms (0, když se hraje lokálně).
        /// Slouží k synchronizaci obrazu u filmů.
        /// </summary>
        public int NetworkLatencyMs => _routeIsNetwork && _networkInfo != null ? _networkInfo.LatencyMs : 0;

        // ZMĚNA: časy se přepočítávají na SKUTEČNÝ čas přehrávání. Soubor
        // o délce 3:00 hraný 3 % rychleji trvá 2:55 - přesně to chceme na
        // displeji vidět, ne původní délku souboru.
        public TimeSpan CurrentTime => _audioFile == null
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(_audioFile.CurrentTime.Ticks / _speedFactor));

        public TimeSpan TotalTime => _audioFile == null
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(_audioFile.TotalTime.Ticks / _speedFactor));

        public float Volume
        {
            get => _audioFile?.Volume ?? 1.0f;
            set { if (_audioFile != null) _audioFile.Volume = Math.Clamp(value, 0f, 1f); }
        }

        public void Load(string filePath)
        {
            Stop();

            _audioFile = new AudioFileReader(filePath);

            // Sestaví řetězec zpracování a výstup (viz BuildOutput níž).
            BuildOutput();
        }

        /// <summary>
        /// Sestaví (nebo znovu sestaví) výstupní řetězec pro právě načtený soubor:
        ///
        ///   soubor -> [varispeed -> převzorkování zpět] -> měření úrovně -> výstup
        ///
        /// kde výstup je buď WASAPI (zvukovka počítače), nebo síť (stereo,
        /// kmitočet podle příjemce).
        ///
        /// Varispeed se do řetězce vkládá JEN když je ladění zapnuté
        /// (_speedFactor se liší od 1). Bez ladění jde signál ze souboru
        /// beze změny přímo do měření a výstupu, přesně jako dřív.
        ///
        /// Jak varispeed funguje: soubor má vzorkovací kmitočet např. 44100 Hz.
        /// DeclaredRateSampleProvider tytéž vzorky pouze "prohlásí" za
        /// záznam s kmitočtem 44100 x faktor, tedy s jiným časovým krokem.
        /// WdlResamplingSampleProvider pak tenhle záznam převede zpátky na
        /// původních 44100 Hz, a právě tím se zkrátí (nebo prodlouží) doba
        /// přehrávání - stejný jev jako zrychlení gramofonové desky nebo
        /// pásku. Výška tónu i tempo se mění společně o stejný poměr.
        /// </summary>
        private void BuildOutput()
        {
            if (_audioFile == null) return;

            // Starou trasu (je-li) zrušíme, ale SOUBOR ponecháme, aby se
            // nezměnila pozice v něm. Událost o ukončení se odpojí dřív než
            // Stop(), jinak by zrušení vyvolalo falešné "doehráno".
            DisposeRoute();

            ISampleProvider chain = _audioFile;

            if (Math.Abs(_speedFactor - 1.0) > 1e-9)
            {
                int originalRate = _audioFile.WaveFormat.SampleRate;
                int declaredRate = Math.Max(1000, (int)Math.Round(originalRate * _speedFactor));

                chain = new DeclaredRateSampleProvider(_audioFile, declaredRate);
                chain = new WdlResamplingSampleProvider(chain, originalRate);
            }

            // 1. Passively inspect raw audio peaks (Pre-Fader / WinAmp style)
            _meteringProvider = new MeteringSampleProvider(chain);
            _meteringProvider.StreamVolume += OnStreamVolume;

            // krok88: zkušební zapojení dvou nezávislých MonoChannel (L a P) -
            // viz PS150.Core.Mixers.StereoMonoChannelSampleProvider. Jen když je
            // zdroj stereo (mono soubory tudy beze změny). Metering zůstává
            // PŘED tímhle zpracováním (pre-fader, viz komentář výš) - VU metr
            // tak pořád ukazuje SUROVOU úroveň souboru, ne po Gain/HPF/Gate/
            // Comp/EQ zpracování.
            ISampleProvider postChannelStrip = _meteringProvider;
            _stereoChannelStrip = null;
            if (_meteringProvider.WaveFormat.Channels == 2)
            {
                _stereoChannelStrip = new StereoMonoChannelSampleProvider(_meteringProvider);
                postChannelStrip = _stereoChannelStrip;
            }

            // 2. Výstup: síť, je-li externí zvukovka k dispozici; jinak (nebo při
            //    jakékoliv chybě) zvukovka počítače.
            bool network = false;

            if (_networkEndpoint != null && _networkInfo != null)
            {
                try
                {
                    _route = CreateNetworkRoute(postChannelStrip);
                    network = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioPlayer] Síťový výstup se nepodařilo vytvořit: {ex.Message}");
                    DisposeNetworkOutput();
                    _route = null;
                }
            }

            _route ??= new WasapiRoute(postChannelStrip);
            _routeIsNetwork = network;
            _route.Ended += OnRouteEnded;
        }

        private IAudioRoute CreateNetworkRoute(ISampleProvider source)
        {
            ReceiverInfo info = _networkInfo!;
            IPEndPoint endpoint = _networkEndpoint!;

            ISampleProvider net = source;

            // Na síť jde vždy stereo v kmitočtu, který příjemce očekává.
            if (net.WaveFormat.Channels == 1)
            {
                net = new MonoToStereoSampleProvider(net);
            }
            else if (net.WaveFormat.Channels > 2)
            {
                var mux = new MultiplexingSampleProvider(new[] { net }, 2);
                mux.ConnectInputToOutput(0, 0);
                mux.ConnectInputToOutput(1, 1);
                net = mux;
            }

            if (net.WaveFormat.SampleRate != info.SampleRate)
            {
                net = new WdlResamplingSampleProvider(net, info.SampleRate);
            }

            if (_networkOutput != null && _networkOutput.SampleRate != info.SampleRate)
            {
                DisposeNetworkOutput();
            }

            _networkOutput ??= new NetworkAudioOutput(endpoint.Address.ToString(), endpoint.Port, info.SampleRate, 2);

            return new NetworkRoute(net, _networkOutput, SynchronizationContext.Current);
        }

        private void DisposeRoute()
        {
            if (_route == null) return;

            _route.Ended -= OnRouteEnded;
            _route.Stop();
            _route.Dispose();
            _route = null;
            _routeIsNetwork = false;
        }

        private void DisposeNetworkOutput()
        {
            _networkOutput?.Dispose();
            _networkOutput = null;
        }

        /// <summary>
        /// NOVÉ: Nastaví (nebo zruší) cíl pro přehrávání po síti. Volá se, když
        /// se na síti objeví nebo zmizí externí zvukovka (viz AudioReceiverWatcher).
        /// Funguje i za chodu: výstup se přepne a přehrávání pokračuje na
        /// stejném místě souboru a ve stejném stavu (hraje / pauza).
        /// </summary>
        public void SetNetworkTarget(IPEndPoint? endpoint, ReceiverInfo? info)
        {
            bool wasNetwork = _networkEndpoint != null && _networkInfo != null;

            _networkEndpoint = endpoint;
            _networkInfo = info;

            bool wantNetwork = endpoint != null && info != null;

            if (!wantNetwork) DisposeNetworkOutput();

            // Nic není načtené -> hodnota se použije při příštím Load().
            if (_audioFile == null || _route == null) return;

            // Jen se obnovily parametry příjemce a trasa už je ta správná.
            if (wasNetwork == wantNetwork && _routeIsNetwork == wantNetwork) return;

            RebuildKeepingState();
        }

        /// <summary>
        /// NOVÉ: Nastaví poměr rychlosti přehrávání (ladění varispeedem).
        /// 1.0 = beze změny, 1.0595 = o půltón výš a o 5,95 % rychleji.
        /// Funguje i za chodu: výstup se krátce přeruší, sestaví znovu a
        /// přehrávání pokračuje na stejném místě souboru a ve stejném
        /// stavu (hraje / pauza).
        /// </summary>
        public void SetSpeedFactor(double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor)) return;

            factor = Math.Clamp(factor, 0.5, 2.0);
            if (Math.Abs(factor - _speedFactor) < 1e-9) return;

            _speedFactor = factor;

            // Nic nehraje a nic není načtené -> jen si hodnotu zapamatujeme
            // pro příští Load().
            if (_audioFile == null || _route == null) return;

            RebuildKeepingState();
        }

        /// <summary>Znovu sestaví výstup a vrátí ho do stavu, v jakém byl (hraje / pauza).</summary>
        private void RebuildKeepingState()
        {
            PlaybackState stateBefore = _route?.State ?? PlaybackState.Stopped;

            BuildOutput();

            if (stateBefore == PlaybackState.Playing)
            {
                _route?.Play();
            }
            else if (stateBefore == PlaybackState.Paused)
            {
                _route?.Play();
                _route?.Pause();
            }
        }

        private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
        {
            // Pre-fader peak capture (unaffected by master output volume)
            if (e.MaxSampleValues.Length > 0) MaxLeftPeak = e.MaxSampleValues[0];
            if (e.MaxSampleValues.Length > 1) MaxRightPeak = e.MaxSampleValues[1];
            else MaxRightPeak = MaxLeftPeak;
        }

        private void OnRouteEnded()
        {
            // Check if we reached the end of the file naturally
            if (_audioFile != null && _audioFile.CurrentTime >= _audioFile.TotalTime - TimeSpan.FromMilliseconds(500))
            {
                PlaybackEnded?.Invoke();
            }
        }

        public void Play() => _route?.Play();
        public void Pause() => _route?.Pause();

        public void Stop()
        {
            DisposeRoute();

            _audioFile?.Dispose();
            _audioFile = null;

            MaxLeftPeak = 0f;
            MaxRightPeak = 0f;
        }

        /// <summary>
        /// Převine o daný počet SKUTEČNÝCH sekund (stejně jako ukazuje displej).
        /// ZMĚNA: při zapnutém ladění odpovídá jedna skutečná sekunda
        /// (_speedFactor) sekundám původního souboru, proto se posun v souboru
        /// násobí faktorem.
        /// </summary>
        public void Seek(double offsetSeconds)
        {
            if (_audioFile == null) return;

            // Vypočítáme nový čas (v čase původního souboru)
            var newTime = _audioFile.CurrentTime.Add(TimeSpan.FromSeconds(offsetSeconds * _speedFactor));

            // Ošetření hranic (nesmíme jít pod 0 ani nad celkovou délku)
            if (newTime < TimeSpan.Zero)
            {
                newTime = TimeSpan.Zero;
            }
            else if (newTime > _audioFile.TotalTime)
            {
                newTime = _audioFile.TotalTime;
            }

            _audioFile.CurrentTime = newTime;
        }

        public (float left, float right) ReadPeakLevels()
        {
            if (!IsPlaying) return (0f, 0f);
            return (MaxLeftPeak, MaxRightPeak);
        }

        public void Dispose()
        {
            Stop();
            DisposeNetworkOutput();
        }

        // ==================================================================
        // Trasy výstupu
        // ==================================================================

        private interface IAudioRoute : IDisposable
        {
            PlaybackState State { get; }
            void Play();
            void Pause();
            void Stop();

            /// <summary>Přehrávání skončilo (soubor došel, nebo ho zastavilo zařízení).</summary>
            event Action? Ended;
        }

        /// <summary>Zvuková karta počítače přes WASAPI (stejně jako dřív).</summary>
        private sealed class WasapiRoute : IAudioRoute
        {
            private readonly WasapiOut _out;

            public event Action? Ended;

            public WasapiRoute(ISampleProvider source)
            {
                _out = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                _out.Init(source);
                _out.PlaybackStopped += OnStopped;
            }

            private void OnStopped(object? sender, StoppedEventArgs e) => Ended?.Invoke();

            public PlaybackState State => _out.PlaybackState;
            public void Play() => _out.Play();
            public void Pause() => _out.Pause();

            public void Stop() => _out.Stop();

            public void Dispose()
            {
                _out.PlaybackStopped -= OnStopped;
                _out.Dispose();
            }
        }

        /// <summary>
        /// Síť: vlastní vlákno čte hotové vzorky z řetězce po paketech a posílá
        /// je přes NetworkAudioOutput, který sám hlídá tempo (reálný čas).
        /// Pauza vlákno jen uspí - pakety se přestanou posílat a příjemce
        /// po vyprázdnění bufferu ztichne.
        /// </summary>
        private sealed class NetworkRoute : IAudioRoute
        {
            private readonly ISampleProvider _source;
            private readonly NetworkAudioOutput _output;
            private readonly SynchronizationContext? _context;

            private Thread? _thread;
            private volatile PlaybackState _state = PlaybackState.Stopped;
            private volatile bool _quit;

            public event Action? Ended;

            public NetworkRoute(ISampleProvider source, NetworkAudioOutput output, SynchronizationContext? context)
            {
                _source = source;
                _output = output;
                _context = context;
            }

            public PlaybackState State => _state;

            public void Play()
            {
                if (_thread == null)
                {
                    _quit = false;
                    _state = PlaybackState.Playing;
                    _thread = new Thread(Pump)
                    {
                        IsBackground = true,
                        Name = "AudioPlayer-Network",
                        Priority = ThreadPriority.AboveNormal
                    };
                    _thread.Start();
                }
                else
                {
                    _state = PlaybackState.Playing;
                }
            }

            public void Pause()
            {
                if (_state == PlaybackState.Playing) _state = PlaybackState.Paused;
            }

            public void Stop()
            {
                _quit = true;
                _state = PlaybackState.Stopped;

                Thread? thread = _thread;
                _thread = null;

                if (thread != null && thread != Thread.CurrentThread) thread.Join(1000);
            }

            public void Dispose() => Stop();

            private void Pump()
            {
                var buffer = new float[NetworkDefaults.PacketFrames * 2];
                bool endOfFile = false;

                try
                {
                    while (!_quit)
                    {
                        if (_state != PlaybackState.Playing)
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        int read = _source.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            endOfFile = true;
                            break;
                        }

                        _output.Write(buffer.AsSpan(0, read - read % 2));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioPlayer] Chyba síťového výstupu: {ex.Message}");
                    endOfFile = true;
                }

                if (endOfFile && !_quit)
                {
                    _state = PlaybackState.Stopped;

                    if (_context != null) _context.Post(_ => Ended?.Invoke(), null);
                    else Ended?.Invoke();
                }
            }
        }

        /// <summary>
        /// Pomocník pro varispeed: předává vzorky beze změny, jen mění
        /// WaveFormat - tvrdí o nich, že mají jiný vzorkovací kmitočet, než
        /// ve skutečnosti mají. Samotné převzorkování pak udělá až
        /// WdlResamplingSampleProvider za ním.
        /// </summary>
        private sealed class DeclaredRateSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;

            public WaveFormat WaveFormat { get; }

            public DeclaredRateSampleProvider(ISampleProvider source, int declaredSampleRate)
            {
                _source = source;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(declaredSampleRate, source.WaveFormat.Channels);
            }

            public int Read(float[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
        }
    }
}
