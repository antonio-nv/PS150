using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

namespace PS150.UI.Windows
{
    /// <summary>
    /// Přehrává .mid soubory na PC přes vestavěný Windows syntezátor
    /// "Microsoft GS Wavetable Synth" - natvrdo jako klavír (Acoustic Grand
    /// Piano) na všech 16 kanálech, bez ohledu na to, jaký nástroj je uvedený
    /// přímo v souboru (Program Change eventy ze souboru se ignorují).
    ///
    /// Kanál č. 10 (index 9) je podle standardu General MIDI vždy bicí -
    /// to zajišťuje sám syntezátor (Microsoft GS Wavetable Synth), ne tento
    /// kód. I když mu pošleme "Program Change = klavír" stejně jako všem
    /// ostatním kanálům, na kanálu 10 to interpretuje jako výběr bicí
    /// soupravy, ne nástroje - proto rytmika hraje správně bicími zvuky bez
    /// jakéhokoliv filtrování z naší strany.
    ///
    /// DŮLEŽITÉ: Tahle třída je záměrně úplně oddělená od PS150.Core
    /// (ToneEngine / InputManager / AudioEngine). Core zůstává nedotčený a dál
    /// slouží výhradně pro živé hraní z MIDI-IN klávesnice (varhany, zvony),
    /// s výhledem na budoucí HW nástroj na Raspberry Pi. Přehrávání .mid
    /// souborů na PC je čistě záležitost UI.Windows.
    /// Piano (0–7): Acoustic Grand Piano, Bright Acoustic Piano, Electric Grand Piano, Honky-tonk Piano, Electric Piano 1, Electric Piano 2, Harpsichord, Clavinet
    /// Chromatic Percussion (8–15): Celesta, Glockenspiel, Music Box, Vibraphone, Marimba, Xylophone, Tubular Bells, Dulcimer
    /// Organ (16–23): Drawbar Organ, Percussive Organ, Rock Organ, Church Organ, Reed Organ, Accordion, Harmonica, Tango Accordion
    /// Guitar (24–31): Acoustic Guitar (nylon), Acoustic Guitar (steel), Electric Guitar (jazz), Electric Guitar (clean), Electric Guitar (muted), Overdriven Guitar, Distortion Guitar, Guitar harmonics
    /// Bass (32–39): Acoustic Bass, Electric Bass (finger), Electric Bass (pick), Fretless Bass, Slap Bass 1, Slap Bass 2, Synth Bass 1, Synth Bass 2
    /// Strings (40–47): Violin, Viola, Cello, Double Bass, Tremolo Strings, Pizzicato Strings, Orchestral Harp, Timpani
    /// Ensemble (48–55): String Ensemble 1, String Ensemble 2, Synth Strings 1, Synth Strings 2, Choir Aahs, Voice Oohs, Synth Voice, Orchestra Hit
    /// Brass (56–63): Trumpet, Trombone, Tuba, Muted Trumpet, French Horn, Brass Section, Synth Brass 1, Synth Brass 2
    /// Reed (64–71): Soprano Sax, Alto Sax, Tenor Sax, Baritone Sax, Oboe, English Horn, Bassoon, Clarinet
    /// Pipe (72–79): Piccolo, Flute, Recorder, Pan Flute, Blown Bottle, Shakuhachi, Whistle, Ocarina
    /// Synth Lead (80–87): Lead 1 (square), Lead 2 (sawtooth), Lead 3 (calliope), Lead 4 (chiff), Lead 5 (charang), Lead 6 (voice), Lead 7 (fifths), Lead 8 (bass + lead)
    /// Synth Pad (88–95): Pad 1 (new age), Pad 2 (warm), Pad 3 (polysynth), Pad 4 (choir), Pad 5 (bowed), Pad 6 (metallic), Pad 7 (halo), Pad 8 (sweep)
    /// Synth Effects (96–103): FX 1 (rain), FX 2 (soundtrack), FX 3 (crystal), FX 4 (atmosphere), FX 5 (brightness), FX 6 (goblins), FX 7 (echoes), FX 8 (sci-fi)
    /// Ethnic (104–111): Sitar, Banjo, Shamisen, Koto, Kalimba, Bagpipe, Fiddle, Shanai
    /// Percussive (112–119): Tinkle Bell, Agogo, Steel Drums, Woodblock, Taiko Drum, Melodic Tom, Synth Drum, Reverse Cymbal
    /// Sound Effects (120–127): Guitar Fret Noise, Breath Noise, Seashore, Bird Tweet, Telephone Ring, Helicopter, Applause, Gunshot
    /// 
    /// </summary>
    public class GmPianoMidiPlayer : IDisposable
    {
        private const string DeviceName = "Microsoft GS Wavetable Synth";

        // Číslo nástroje se posílá po drátě jako hodnota 0-127 (Program Change).
        // Tištěné tabulky GM nástrojů bývají číslované 1-128 pro lidi ("č. 1 =
        // Acoustic Grand Piano"), ale fyzicky se posílá 0 - takže hodnota 0 je
        // správně a NEMĚNIT na 1 (to by poslalo "Bright Acoustic Piano", tedy
        // druhý nástroj v tabulce, místo Acoustic Grand Piano).
        private const int AcousticGrandPiano = 0;

        private const int PanControlNumber = 10;   // GM/MIDI Control Change č. 10 = Pan
        private const int VolumeControlNumber = 7;  // GM/MIDI Control Change č. 7 = Channel Volume
        private const int PanLeft = 0;
        private const int PanCenter = 64;
        private const int PanRight = 127;

        private OutputDevice? _outputDevice;
        private CancellationTokenSource? _cts;
        private Task? _playbackTask;
        private Playback? _playback;

        // Hlavní hlasitost (0-100) ovládaná šipkami nahoru/dolů ve VGA konzoli.
        // Násobí se s hlasitostí, kterou si případně řídí sám soubor (CC7) -
        // viz EventPlayed níž. Výchozí 100 = beze změny.
        private volatile int _masterVolumePercent = 100;

        // Nastaveno v Pause() / zrušeno v Resume() - viz komentář u monitorovací
        // smyčky v PlayAsync níže. Bez tohohle příznaku by playback.Stop() volané
        // z Pause() smyčku ukončilo úplně stejně, jako kdyby skladba doopravdy
        // dohrála - a VgaEngine by omylem skočila na další soubor.
        private volatile bool _isPausedByUser = false;

        /// <summary>True, pokud je přehrávání aktuálně pozastavené přes Pause().</summary>
        public bool IsPaused => _isPausedByUser;

        // Celková délka aktuálně načteného souboru - spočítaná JEDNOU při
        // načtení (viz PlayAsync), ne odhadovaná. Na rozdíl od komprimovaného
        // zvuku (MP3 apod.) je délka MIDI souboru známá přesně už ze
        // samotných dat (delta-časy + tempo-eventy), žádné dopočítávání
        // z velikosti souboru nebo bitrate.
        private TimeSpan _totalTime = TimeSpan.Zero;
        public TimeSpan TotalTime => _totalTime;

        /// <summary>
        /// Aktuální pozice přehrávání. Čte se přímo z DryWetMidi Playbacku,
        /// takže odráží i posun po Seek() nebo po Pause()/Resume().
        /// </summary>
        public TimeSpan CurrentTime
        {
            get
            {
                var playback = _playback;
                if (playback == null) return TimeSpan.Zero;
                return (TimeSpan)playback.GetCurrentTime<MetricTimeSpan>();
            }
        }

        /// <summary>Vyvoláno při rozeznění noty. Parametry: (kanál 0-15, MIDI číslo noty).</summary>
        public event Action<int, int>? NoteOnRaised;

        /// <summary>Vyvoláno při doznění noty. Parametry: (kanál 0-15, MIDI číslo noty).</summary>
        public event Action<int, int>? NoteOffRaised;

        /// <summary>
        /// Vyvoláno jednou po načtení souboru se seznamem kanálů (notových
        /// osnov), které soubor používá, seřazeným vzestupně.
        /// </summary>
        public event Action<int[]>? ChannelsDetected;

        /// <summary>
        /// Vyvoláno, když skladba dohraje sama do konce (NE když ji přeruší
        /// Stop() kvůli přechodu na jiný soubor nebo ukončení aplikace).
        /// </summary>
        public event Action? PlaybackFinishedNaturally;

        /// <summary>
        /// Vyvoláno, když se soubor nepodaří načíst/přehrát (poškozený nebo
        /// nestandardní .mid soubor). Po tomto eventu se ZÁMĚRNĚ
        /// NEpokračuje na další soubor - zůstáváme stát na tom problémovém,
        /// ať se dá podle chybové zprávy diagnostikovat.
        /// </summary>
        public event Action<Exception>? PlaybackFailed;

        public async Task PlayAsync(string filePath)
        {
            if (!File.Exists(filePath)) return;

            Stop();
            LastSeekDiagnostic = null;
            _totalTime = TimeSpan.Zero;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            MidiFile midiFile;
            int[] usedChannels;

            try
            {
                _outputDevice = OutputDevice.GetByName(DeviceName);
                if (_outputDevice == null)
                {
                    throw new InvalidOperationException(
                        $"Výstupní zařízení '{DeviceName}' nebylo na tomto PC nalezeno.");
                }

                midiFile = MidiFile.Read(filePath);

                // Celková délka - spočítaná přesně z dat souboru (viz komentář
                // u TotalTime výše), jednou hned po načtení.
                _totalTime = (TimeSpan)midiFile.GetDuration<MetricTimeSpan>();

                // Zjistíme, které kanály (notové osnovy) soubor vůbec používá - pro
                // trvalé zobrazení všech osnov ve VGA konzoli a pro ping-pong
                // panorámu níž.
                usedChannels = midiFile.GetTimedEvents()
                    .Select(te => te.Event)
                    .OfType<NoteOnEvent>()
                    .Select(n => (int)n.Channel)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToArray();
            }
            catch (Exception ex)
            {
                // Soubor se nepodařilo ani načíst/rozebrat - nahlásíme chybu
                // a NEpokračujeme dál (viz komentář u PlaybackFailed).
                PlaybackFailed?.Invoke(ex);
                return;
            }

            ChannelsDetected?.Invoke(usedChannels);

            // Natvrdo nastavíme klavír na všech 16 kanálech ještě PŘED přehráváním,
            // ať soubor obsahuje jakékoliv nástroje - Program Change eventy ze
            // souboru samotného níž v EventPlayed prostě zahodíme, takže naše
            // nastavení nikdo nepřepíše.
            for (int channel = 0; channel <= 15; channel++)
            {
                _outputDevice!.SendEvent(new ProgramChangeEvent((SevenBitNumber)AcousticGrandPiano)
                {
                    Channel = (FourBitNumber)channel
                });
                // Počáteční hlasitost podle aktuálního nastavení VGA konzole.
                _outputDevice.SendEvent(new ControlChangeEvent(
                    (SevenBitNumber)VolumeControlNumber, (SevenBitNumber)ScaleToMidiVolume(127))
                {
                    Channel = (FourBitNumber)channel
                });
            }

            // Ping-pong panoráma mezi jednotlivými osnovami: je-li osnova jen
            // jedna, necháme ji na středu (zní stejně silně na obou kanálech).
            // Je-li osnov víc, střídáme je pravidelně vlevo/vpravo podle pořadí
            // čísla kanálu. Pan eventy ze souboru samotného níž v EventPlayed
            // zahazujeme, ať nám tohle rozložení později v playbacku nikdo
            // nepřepíše.
            if (usedChannels.Length == 1)
            {
                SendPan(usedChannels[0], PanCenter);
            }
            else if (usedChannels.Length > 1)
            {
                for (int i = 0; i < usedChannels.Length; i++)
                {
                    SendPan(usedChannels[i], (i % 2 == 0) ? PanLeft : PanRight);
                }
            }

            _playbackTask = Task.Run(() =>
            {
                try
                {
                    using var playback = new Playback(midiFile.GetTimedEvents(), midiFile.GetTempoMap());
                    _playback = playback;

                    playback.EventPlayed += (sender, e) =>
                    {
                        if (_outputDevice == null) return;

                        switch (e.Event)
                        {
                            case ProgramChangeEvent:
                                // Ignorováno - nástroj je natvrdo klavír, viz výše.
                                break;

                            case ControlChangeEvent panCc when panCc.ControlNumber == PanControlNumber:
                                // Ignorováno - panorámu řídíme sami (ping-pong), viz výše.
                                break;

                            case ControlChangeEvent volCc when volCc.ControlNumber == VolumeControlNumber:
                                // ZKUŠEBNĚ ignorováno (stejně jako Pan a ProgramChange
                                // výše) - dřív se tu hlasitost jednotlivé osnovy ze
                                // souboru (CC7, včetně crescend/decrescend během
                                // hraní) násobila s hlavní hlasitostí. Teď se
                                // neposílá vůbec nic, takže kanálu zůstává napořád
                                // ta pevná hodnota nastavená při startu přehrávání
                                // (100 % × hlavní hlasitost z VGA konzole, viz výše
                                // "Počáteční hlasitost..."). Zvuková dynamika
                                // jednotlivých not (velocity) tímhle není dotčená -
                                // řeší jen kanálovou/celkovou hlasitost.
                                break;

                            case NoteOnEvent noteOn:
                                _outputDevice.SendEvent(noteOn);
                                NoteOnRaised?.Invoke(noteOn.Channel, noteOn.NoteNumber);
                                break;

                            case NoteOffEvent noteOff:
                                _outputDevice.SendEvent(noteOff);
                                NoteOffRaised?.Invoke(noteOff.Channel, noteOff.NoteNumber);
                                break;

                            default:
                                _outputDevice.SendEvent(e.Event);
                                break;
                        }
                    };

                    playback.Start();

                    // POZOR: podmínka "|| _isPausedByUser" je záměrná a DŮLEŽITÁ.
                    // Pause() níže volá playback.Stop() (DryWetMidi to bere jako
                    // "pozastav, ale nezahazuj pozici" - lze pak znovu Start()).
                    // Jenže playback.IsRunning je po tomhle Stop() taky false -
                    // BEZE ZKOUŠKY _isPausedByUser by tahle smyčka okamžitě
                    // skončila stejně, jako kdyby skladba doopravdy dohrála celá,
                    // a kód pod smyčkou by omylem vystřelil
                    // PlaybackFinishedNaturally (VgaEngine by pak skočila na
                    // další soubor, místo aby zůstala pozastavená na místě).
                    while (playback.IsRunning || _isPausedByUser)
                    {
                        if (token.IsCancellationRequested)
                        {
                            playback.Stop();
                            break;
                        }
                        Thread.Sleep(10);
                    }

                    // Skladba dohrála sama od sebe (nebylo to přerušené zvenčí
                    // přes Stop()) -> dáme vědět, ať VgaEngine skočí na další
                    // soubor, stejně jako to dělá u zvukových souborů.
                    if (!token.IsCancellationRequested)
                    {
                        PlaybackFinishedNaturally?.Invoke();
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // Chyba až za běhu (např. neplatná/nestandardní data
                    // uprostřed souboru) - nahlásíme a NEpokračujeme na další
                    // soubor, ať se dá problémový soubor diagnostikovat.
                    PlaybackFailed?.Invoke(ex);
                }
                finally
                {
                    _playback = null;
                }
            }, token);

            try
            {
                await _playbackTask;
            }
            catch (OperationCanceledException)
            {
                // Očekávané chování při Stop() - nejde o chybu.
            }
        }

        /// <summary>
        /// Text poslední chyby při převíjení (NENÍ fatální jako PlaybackFailed -
        /// přehrávání souboru pokračuje dál, jen konkrétní pokus o převinutí
        /// selhal). Vystaveno jako veřejná vlastnost místo jen Debug.WriteLine,
        /// protože Debug.WriteLine je v Release buildu neviditelné - a právě
        /// v Release tohle testujeme.
        /// </summary>
        public string? LastSeekDiagnostic { get; private set; }

        /// <summary>
        /// Převine přehrávání o daný počet sekund dopředu (kladné číslo) nebo
        /// dozadu (záporné). Rozehrané noty se násilím neztišují - necháme je
        /// doznít přirozeně, i za cenu drobného "mišmaše" těsně po převinutí.
        /// </summary>
        public void Seek(double deltaSeconds)
        {
            var playback = _playback;
            if (playback == null)
            {
                LastSeekDiagnostic = "Seek() zavolán, ale přehrávání zrovna neběží (_playback == null).";
                return;
            }

            try
            {
                if (deltaSeconds >= 0)
                {
                    playback.MoveForward(new MetricTimeSpan(TimeSpan.FromSeconds(deltaSeconds)));
                }
                else
                {
                    playback.MoveBack(new MetricTimeSpan(TimeSpan.FromSeconds(-deltaSeconds)));
                }

                LastSeekDiagnostic = null;
            }
            catch (Exception ex)
            {
                LastSeekDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[GmPianoMidiPlayer] Chyba při převíjení: {LastSeekDiagnostic}");
            }
        }

        /// <summary>
        /// Pozastaví přehrávání na místě, beze ztráty pozice v souboru -
        /// na rozdíl od Stop() (ten pozici zahodí a uvolní výstupní zařízení).
        /// Volá se z mezerníku ve VGA konzoli, když je aktivní MIDI režim.
        /// </summary>
        public void Pause()
        {
            _isPausedByUser = true;
            _playback?.Stop();
        }

        /// <summary>Obnoví přehrávání po Pause(), od stejného místa, kde se přerušilo.</summary>
        public void Resume()
        {
            _isPausedByUser = false;
            _playback?.Start();
        }

        /// <summary>
        /// Nastaví hlavní hlasitost (0-100), typicky navázáno na šipky
        /// nahoru/dolů ve VGA konzoli. Projeví se ihned na všech 16 kanálech
        /// a dál se násobí s případnou vlastní dynamikou (CC7) ze souboru.
        /// </summary>
        public void SetVolume(int volumePercent)
        {
            _masterVolumePercent = Math.Clamp(volumePercent, 0, 100);

            if (_outputDevice == null) return;

            int midiVolume = ScaleToMidiVolume(127);
            for (int channel = 0; channel <= 15; channel++)
            {
                _outputDevice.SendEvent(new ControlChangeEvent(
                    (SevenBitNumber)VolumeControlNumber, (SevenBitNumber)midiVolume)
                {
                    Channel = (FourBitNumber)channel
                });
            }
        }

        private int ScaleToMidiVolume(int originalValue0To127)
        {
            double scaled = originalValue0To127 * (_masterVolumePercent / 100.0);
            return (int)Math.Round(Math.Clamp(scaled, 0, 127));
        }

        private void SendPan(int channel, int panValue)
        {
            _outputDevice?.SendEvent(new ControlChangeEvent((SevenBitNumber)PanControlNumber, (SevenBitNumber)panValue)
            {
                Channel = (FourBitNumber)channel
            });
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();

                // KLÍČOVÉ: počkáme, až přehrávací vlákno doopravdy skončí -
                // zavolá si vlastní playback.Stop(), který ještě chce poslat
                // NoteOff na doznívající noty přes _outputDevice. Teprve POTOM
                // smíme zařízení uvolnit. Když se to dělalo v opačném pořadí
                // (uvolnit hned a nečekat), hrozila AccessViolationException,
                // protože DryWetMidi poslal NoteOff na už uvolněné nativní
                // zařízení - přesně tohle dřív padalo.
                try
                {
                    _playbackTask?.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // OperationCanceledException zabalená přes Task.Wait - očekávané.
                }

                _cts.Dispose();
                _cts = null;
            }

            _playbackTask = null;

            _outputDevice?.Dispose();
            _outputDevice = null;

            _totalTime = TimeSpan.Zero;
        }

        public void Dispose() => Stop();
    }
}
