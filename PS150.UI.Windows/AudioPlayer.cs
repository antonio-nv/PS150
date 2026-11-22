using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PS150.UI.Windows
{
    public class AudioPlayer : IDisposable
    {
        private WasapiOut? _wasapiOut;
        private AudioFileReader? _audioFile;
        private MeteringSampleProvider? _meteringProvider;

        // NOVÉ: ladění změnou rychlosti (varispeed). 1.0 = beze změny.
        // Hodnota se pamatuje i mezi soubory - nastaví se jednou (SetSpeedFactor)
        // a platí pro každý další Load(), dokud ji někdo nezmění.
        private double _speedFactor = 1.0;

        public event Action? PlaybackEnded;

        public float MaxLeftPeak { get; private set; }
        public float MaxRightPeak { get; private set; }

        /// <summary>Aktuální poměr rychlosti přehrávání (1.0 = původní).</summary>
        public double SpeedFactor => _speedFactor;

        public bool IsPlaying => _wasapiOut?.PlaybackState == PlaybackState.Playing;

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

            // Sestaví řetězec zpracování a WASAPI výstup (viz BuildOutput níž).
            BuildOutput();
        }

        /// <summary>
        /// NOVÉ: Sestaví (nebo znovu sestaví) výstupní řetězec pro právě
        /// načtený soubor:
        ///
        ///   soubor -> [varispeed -> převzorkování zpět] -> měření úrovně -> WASAPI
        ///
        /// Prostřední část se do řetězce vkládá JEN když je ladění zapnuté
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

            // Starý výstup (je-li) zrušíme, ale SOUBOR ponecháme, aby se
            // nezměnila pozice v něm. PlaybackStopped odpojíme dřív než
            // Stop(), jinak by zrušení vyvolalo falešné "doehráno".
            if (_wasapiOut != null)
            {
                _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
                _wasapiOut.Stop();
                _wasapiOut.Dispose();
                _wasapiOut = null;
            }

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

            // 2. Initialize WASAPI with the sample provider
            _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            _wasapiOut.Init(_meteringProvider);
            _wasapiOut.PlaybackStopped += OnPlaybackStopped; // <-- Add this!
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
            if (_audioFile == null || _wasapiOut == null) return;

            PlaybackState stateBefore = _wasapiOut.PlaybackState;

            BuildOutput();

            if (stateBefore == PlaybackState.Playing)
            {
                _wasapiOut?.Play();
            }
            else if (stateBefore == PlaybackState.Paused)
            {
                _wasapiOut?.Play();
                _wasapiOut?.Pause();
            }
        }

        private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
        {
            // Pre-fader peak capture (unaffected by master output volume)
            if (e.MaxSampleValues.Length > 0) MaxLeftPeak = e.MaxSampleValues[0];
            if (e.MaxSampleValues.Length > 1) MaxRightPeak = e.MaxSampleValues[1];
            else MaxRightPeak = MaxLeftPeak;
        }


        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // Check if we reached the end of the file naturally
            if (_audioFile != null && _audioFile.CurrentTime >= _audioFile.TotalTime - TimeSpan.FromMilliseconds(500))
            {
                PlaybackEnded?.Invoke();
            }
        }


        public void Play() => _wasapiOut?.Play();
        public void Pause() => _wasapiOut?.Pause();

        public void Stop()
        {
            _wasapiOut?.Stop();
            _wasapiOut?.Dispose();
            _wasapiOut = null;

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

        public void Dispose() => Stop();

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
