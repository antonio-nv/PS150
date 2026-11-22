using System;

namespace PS150.Core.Network
{
    /// <summary>
    /// Příjmový buffer: ze sítě přichází pakety nepravidelně (zpoždění se mění,
    /// některé se ztratí, jiné přijdou v jiném pořadí), ale zvuková karta chce
    /// data stále ve stejném tempu. Buffer to srovná:
    ///
    ///  - Pakety zapisuje podle čísla prvního snímku do kruhového bufferu,
    ///    takže přeházené pakety zapadnou na své místo a chybějící úsek zůstane
    ///    tichý (ztráta se projeví krátkým výpadkem, ne posunem celého zvuku).
    ///  - Přehrávání začne, až je nashromážděno "cílové zpoždění"
    ///    (targetDelayMs); to je rezerva proti kolísání sítě. Na kabelu stačí
    ///    ~20 ms, na WiFi je rozumné 50-100 ms.
    ///  - Hodiny počítače a zvukové karty nikdy nejdou přesně stejně (rozdíl
    ///    jednotek až desítek ppm). Bez korekce by se buffer pomalu vyprázdnil
    ///    nebo naplnil. Proto se tempo čtení řídí jemně podle zaplnění
    ///    (lineární interpolace, odchylka max. ±1000 ppm = 0,1 %, neslyšitelné).
    ///  - Dojde-li buffer úplně (underrun), vypne se zvuk a po nashromáždění
    ///    rezervy se naváže na nejnovější data. Příliš zaplněný buffer se
    ///    srovná skokem (zpoždění se nesmí donekonečna hromadit).
    ///
    /// Pakety vkládá síťové vlákno (Push), čte vlákno zvukové karty (Read).
    /// Obě volání jsou chráněná zámkem; držení zámku je krátké.
    /// </summary>
    public sealed class AudioJitterBuffer
    {
        private const int CapacityBits = 17;                     // 131072 snímků = 2,7 s při 48 kHz
        private const int Capacity = 1 << CapacityBits;
        private const int Mask = Capacity - 1;
        private const double MaxRatePpm = 1000.0;
        private const double PpmPerFrameOfError = 1.0;           // 1 ppm na každý snímek odchylky od cíle
        private const double FillSmoothing = 0.02;               // vyhlazení zaplnění (na jedno čtení)

        private readonly object _lock = new object();
        private readonly float[] _storage;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly int _targetFrames;

        private bool _hasStream;
        private uint _streamId;
        private bool _sequenceInitialized;
        private uint _expectedSequence;

        private long _highestEnd;          // první snímek, který ještě nedorazil (nejvyšší konec dosud přijatého)
        private double _readPos;           // aktuální pozice přehrávání (zlomek snímku kvůli korekci tempa)
        private long _clearedUpTo;         // snímky před touto pozicí jsou v bufferu vynulované
        private bool _playing;
        private bool _resyncOnStart;
        private double _smoothFill;

        // ---- statistiky ----
        public long PacketsReceived { get; private set; }
        public long PacketsLost { get; private set; }
        public long PacketsReordered { get; private set; }
        public long PacketsLate { get; private set; }
        public long PacketsRejected { get; private set; }
        public long Underruns { get; private set; }
        public long Resyncs { get; private set; }
        public double RatePpm { get; private set; }

        public AudioJitterBuffer(int channels, int sampleRate, int targetDelayMs)
        {
            if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (targetDelayMs < 5 || targetDelayMs > 1000) throw new ArgumentOutOfRangeException(nameof(targetDelayMs));

            _channels = channels;
            _sampleRate = sampleRate;
            _targetFrames = (int)((long)sampleRate * targetDelayMs / 1000);
            _storage = new float[Capacity * channels];
        }

        public int TargetDelayFrames => _targetFrames;

        public bool IsPlaying
        {
            get { lock (_lock) return _playing; }
        }

        /// <summary>Kolik snímků je právě nashromážděno před přehrávací pozicí.</summary>
        public double FillFrames
        {
            get { lock (_lock) return _hasStream ? Math.Max(0.0, _highestEnd - _readPos) : 0.0; }
        }

        public double FillMs => FillFrames * 1000.0 / _sampleRate;

        /// <summary>Vloží přijatý paket (volá síťové vlákno).</summary>
        public void Push(AudioPacket packet)
        {
            if (packet.Channels != _channels || packet.SampleRate != _sampleRate)
            {
                lock (_lock) PacketsRejected++;
                return;
            }

            lock (_lock)
            {
                PacketsReceived++;

                if (!_hasStream || packet.StreamId != _streamId)
                {
                    StartNewStream(packet);
                }

                CountSequence(packet.Sequence);

                long start = packet.FirstFrame;
                long end = start + packet.FrameCount;
                long minFrame = (long)Math.Floor(_readPos);

                if (end <= minFrame)
                {
                    PacketsLate++;      // dorazil, až když se jeho úsek už přehrál
                    return;
                }

                // Zvukový proud se vzdálil (např. odesílatel dlouho mlčel): srovnáme se na něj.
                if (start > minFrame + Capacity / 2)
                {
                    ResyncTo(start);
                    minFrame = (long)Math.Floor(_readPos);
                }

                int from = (int)Math.Max(0L, minFrame - start);   // část paketu, která už je minulost
                for (int f = from; f < packet.FrameCount; f++)
                {
                    int slot = (int)((start + f) & Mask) * _channels;
                    int src = f * _channels;
                    for (int c = 0; c < _channels; c++)
                    {
                        _storage[slot + c] = packet.Samples[src + c];
                    }
                }

                if (end > _highestEnd) _highestEnd = end;

                if (!_playing && _highestEnd - _readPos >= _targetFrames)
                {
                    if (_resyncOnStart)
                    {
                        // po výpadku se naváže na nejnovější data, ne na starou pozici
                        double newPos = _highestEnd - _targetFrames;
                        if (newPos > _readPos) AdvanceTo(newPos);
                        _resyncOnStart = false;
                    }

                    _playing = true;
                    _smoothFill = _highestEnd - _readPos;
                }
            }
        }

        /// <summary>
        /// Naplní <paramref name="output"/> zvukem (prokládaně po kanálech).
        /// Volá vlákno zvukové karty; nikdy neblokuje déle než kratičký zámek.
        /// Když zvuk není k dispozici, vyplní ticho.
        /// </summary>
        public void Read(Span<float> output)
        {
            int frames = output.Length / _channels;

            lock (_lock)
            {
                if (!_playing || frames == 0)
                {
                    output.Clear();
                    return;
                }

                // ---- řízení tempa podle zaplnění ----
                double fill = _highestEnd - _readPos;
                _smoothFill += (fill - _smoothFill) * FillSmoothing;

                double ppm = (_smoothFill - _targetFrames) * PpmPerFrameOfError;
                if (ppm > MaxRatePpm) ppm = MaxRatePpm;
                else if (ppm < -MaxRatePpm) ppm = -MaxRatePpm;
                RatePpm = ppm;
                double rate = 1.0 + ppm * 1e-6;

                // Buffer se přespříliš naplnil (např. po zpoždění v síti): zahodíme starší část.
                if (fill > _targetFrames * 4L + frames)
                {
                    AdvanceTo(_highestEnd - _targetFrames);
                    Resyncs++;
                    _smoothFill = _targetFrames;
                }

                int done = 0;
                for (; done < frames; done++)
                {
                    long i = (long)Math.Floor(_readPos);
                    if (i + 1 >= _highestEnd)
                    {
                        // došla data
                        Underruns++;
                        _playing = false;
                        _resyncOnStart = true;
                        break;
                    }

                    double frac = _readPos - i;
                    int slotA = (int)(i & Mask) * _channels;
                    int slotB = (int)((i + 1) & Mask) * _channels;
                    int dst = done * _channels;

                    for (int c = 0; c < _channels; c++)
                    {
                        float a = _storage[slotA + c];
                        float b = _storage[slotB + c];
                        output[dst + c] = (float)(a + (b - a) * frac);
                    }

                    _readPos += rate;
                }

                if (done < frames)
                {
                    output.Slice(done * _channels).Clear();
                }

                ClearConsumed();
            }
        }

        // ---- vnitřní pomocné metody (volat jen pod zámkem) ----

        private void StartNewStream(AudioPacket first)
        {
            Array.Clear(_storage, 0, _storage.Length);
            _hasStream = true;
            _streamId = first.StreamId;
            _sequenceInitialized = false;
            _highestEnd = first.FirstFrame;
            _readPos = first.FirstFrame;
            _clearedUpTo = first.FirstFrame;
            _playing = false;
            _resyncOnStart = false;
            _smoothFill = 0.0;
        }

        private void ResyncTo(long frame)
        {
            Array.Clear(_storage, 0, _storage.Length);
            _highestEnd = frame;
            _readPos = frame;
            _clearedUpTo = frame;
            _playing = false;
            _resyncOnStart = false;
            Resyncs++;
        }

        private void CountSequence(uint sequence)
        {
            if (!_sequenceInitialized)
            {
                _sequenceInitialized = true;
                _expectedSequence = sequence + 1;
                return;
            }

            if (sequence == _expectedSequence)
            {
                _expectedSequence++;
                return;
            }

            uint ahead = unchecked(sequence - _expectedSequence);
            if (ahead < 0x80000000u)
            {
                PacketsLost += ahead;        // mezera = chybějící pakety
                _expectedSequence = sequence + 1;
            }
            else
            {
                PacketsReordered++;          // opozdilec: už jsme ho započítali jako ztracený
                if (PacketsLost > 0) PacketsLost--;
            }
        }

        /// <summary>Posune přehrávací pozici dopředu a vynuluje přeskočený úsek.</summary>
        private void AdvanceTo(double newPos)
        {
            _readPos = newPos;
            ClearConsumed();
        }

        /// <summary>Vynuluje snímky, které se už nebudou číst, aby se místo nich při dalším obletu nepřehrála stará data.</summary>
        private void ClearConsumed()
        {
            long upTo = (long)Math.Floor(_readPos);     // snímek upTo je ještě potřeba (interpolace)

            if (upTo - _clearedUpTo >= Capacity)
            {
                Array.Clear(_storage, 0, _storage.Length);
                _clearedUpTo = upTo;
                return;
            }

            while (_clearedUpTo < upTo)
            {
                int slot = (int)(_clearedUpTo & Mask) * _channels;
                Array.Clear(_storage, slot, _channels);
                _clearedUpTo++;
            }
        }
    }
}
