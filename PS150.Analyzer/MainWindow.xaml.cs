using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.Wave;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using ScottPlot;

using Window = System.Windows.Window;
using MediaColor = System.Windows.Media.Color; // ALIAS PRO VYŘEŠENÍ CHYBY CS0104

namespace PS150.Analyzer
{
    public partial class MainWindow : Window
    {
        private WaveInEvent? _waveIn;
        private const int SampleRate = 44100;

        private int _fftSize = 8192;
        private float[] _sampleBuffer = new float[8192];
        private int _bufferIndex = 0;
        private float _maxPeak = 0;

        private DateTime _lastRenderTime = DateTime.MinValue;
        private bool _isFrozen = false;
        private bool _waitForSnap = false;
        private bool _isMeasuringSnap = false;

        public MainWindow()
        {
            InitializeComponent();
            LoadMicrophones();
            InitFftOptions();
            InitNoteSelectors();
            SetupPlot();
            StartAudioCapture();
        }

        // krok24: C, C#, D... anglická konvence - žádné české Cis/Es/H/B, ať
        // nevzniká zbytečná diskuse, které písmeno znamená co.
        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        private void InitNoteSelectors()
        {
            ComboNoteName.ItemsSource = NoteNames;
            ComboNoteName.SelectedItem = "A";

            var octaves = System.Linq.Enumerable.Range(-1, 11) // -1 .. 9
                .Select(o => o.ToString(CultureInfo.InvariantCulture)).ToList();
            ComboOctave.ItemsSource = octaves;
            ComboOctave.SelectedItem = "2"; // výchozí A2, jako v dosavadních příkladech

            // krok25: počet předem plánovaných tónů sekvence - 1 = bez aproximace
            // (jako doteď), 2+ = má smysl krok (půltóny) mezi nimi, viz
            // ComboStepCount_SelectionChanged níž.
            ComboStepCount.ItemsSource = System.Linq.Enumerable.Range(1, 8)
                .Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
            ComboStepCount.SelectedItem = "1";

            UpdateHelmholtzLabel(); // pro jistotu, i kdyby SelectionChanged výš nestihl proběhnout
        }

        private void NoteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHelmholtzLabel();

        private int GetStepCount()
        {
            if (ComboStepCount?.SelectedItem is string s && int.TryParse(s, out int n) && n >= 1) return n;
            return 1;
        }

        // krok25: "Krok (půltóny)" dává smysl jen u sekvence 2 a víc tónů -
        // s jedním tónem se žádná aproximace nepoužije, je to prostý jednorázový
        // rozbor jako doteď.
        private void ComboStepCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LblNoteStep == null || TxtNoteStep == null) return; // při startu, než se XAML dotáhne

            var visibility = GetStepCount() >= 2 ? Visibility.Visible : Visibility.Collapsed;
            LblNoteStep.Visibility = visibility;
            TxtNoteStep.Visibility = visibility;
        }

        // krok25: tradiční varhanářské/hudební označení oktávy (subkontra, kontra,
        // velká, malá, jednočárkovaná...) vedle GM čísla - to je ten tvar, kterým
        // se domlouváš s varhaníkem, ne "C#4". Apostrofy místo horních indexů
        // (c', c''...) - běžný a bezpečný zápis, když nejsou k dispozici notové znaky.
        private static readonly string[] CzechNoteNames =
            { "C", "Cis", "D", "Dis", "E", "F", "Fis", "G", "Gis", "A", "B", "H" };

        private static readonly string[] LowOctaveNames =
            { "velká", "kontra", "subkontra", "sub-subkontra" };

        private static readonly string[] HighOctaveNames =
            { "malá", "jednočárkovaná", "dvojčárkovaná", "tříčárkovaná", "čtyřčárkovaná", "pětičárkovaná", "šestičárkovaná" };

        private static string FormatHelmholtz(int midiNote)
        {
            int pitchClass = midiNote % 12;
            int scientificOctave = midiNote / 12 - 1; // stejné číslování jako ComboOctave (c1 = C4 = MIDI 60)
            string baseName = CzechNoteNames[pitchClass];

            if (scientificOctave <= 2)
            {
                int commas = Math.Clamp(2 - scientificOctave, 0, LowOctaveNames.Length - 1);
                string mark = new string(',', commas);
                return $"{baseName}{mark} ({LowOctaveNames[commas]})";
            }
            else
            {
                int ticks = Math.Clamp(scientificOctave - 3, 0, HighOctaveNames.Length - 1);
                string lower = char.ToLowerInvariant(baseName[0]) + baseName.Substring(1);
                string mark = ticks == 0 ? "" : new string('\'', ticks);
                return $"{lower}{mark} ({HighOctaveNames[ticks]})";
            }
        }

        private void UpdateHelmholtzLabel()
        {
            if (TxtHelmholtz == null) return; // při startu, než se XAML dotáhne
            TxtHelmholtz.Text = "= " + FormatHelmholtz(GetSelectedMidiNote());
        }

        // krok24: MIDI číslo z vybrané noty/oktávy (C4 = střední C = 60, stejně jako v Core).
        private int GetSelectedMidiNote()
        {
            int pitchClass = Math.Max(0, ComboNoteName.SelectedIndex);
            int octave = int.Parse((string)(ComboOctave.SelectedItem ?? "2"), CultureInfo.InvariantCulture);
            return (octave + 1) * 12 + pitchClass;
        }

        private void SetSelectedMidiNote(int midiNote)
        {
            midiNote = Math.Clamp(midiNote, 0, 119); // horní mez B9 - víc, než kam ladička varhan sahá
            ComboNoteName.SelectedIndex = midiNote % 12;
            ComboOctave.SelectedItem = (midiNote / 12 - 1).ToString(CultureInfo.InvariantCulture);
        }

        // krok24: "pokračovat sekvencí" - šipky posunou vybranou notu o Krok
        // půltónů (výchozí 4 = tvoje A/Cis/F), ať se tabulkou not nemusí
        // listovat ručně po jedné.
        private void BtnNextNote_Click(object sender, RoutedEventArgs e) => StepNote(+1);
        private void BtnPrevNote_Click(object sender, RoutedEventArgs e) => StepNote(-1);

        private void StepNote(int direction)
        {
            // krok25: u jediného naplánovaného tónu (Kroků=1) nemá interval
            // žádný smysl - šipky prostě jedou po půltónech. Teprve od dvou
            // kroků se použije zadaný interval z (teď viditelného) pole.
            int step = 1;
            if (GetStepCount() >= 2 && int.TryParse(TxtNoteStep.Text, out int parsed) && parsed > 0)
                step = parsed;

            SetSelectedMidiNote(GetSelectedMidiNote() + direction * step);
        }





        private void ApplyMagicAnalysis(double[] freqsLog, double[] magnitudesDb, double[] freqsHz)
        {
            int selectedMode = ComboMagicMode.SelectedIndex;
            string resultText = "";

            switch (selectedMode)
            {
                case 0: // 🎹 VARHANY: Píky seřazené podle síly + násobky (včetně subharmonických < 1.0x)
                    resultText = AnalyzeOrganSubAndHarmonics(freqsHz, magnitudesDb);
                    break;

                case 1: // 🔔 ZVONY: Dominantní inharmonické čáry + koeficienty
                    resultText = AnalyzeBellPikes(freqsHz, magnitudesDb);
                    break;

                case 2: // 🥁 ŠUMY: Detekce vrcholku kopce, šířky základny a spádu v dB
                    resultText = AnalyzeNoiseShape(freqsHz, magnitudesDb);
                    break;
            }

            TxtMagicOutput.Text = resultText;
        }

        // 🎹 KOUZLO 1: VARHANY (krok23: dominanta podle zadané noty, roztřídění
        // VŠECH píků na nejbližší celočíselný násobek s vyřazením ústřelů)
        private string AnalyzeOrganSubAndHarmonics(double[] freqs, double[] dbs)
        {
            var pikes = FindAllPikes(freqs, dbs, -75.0); // Hledáme píky nad -75 dB
            if (pikes.Count == 0) return "[VARHANY] Žádný výrazný tón nenalezen (nízký signál).";

            // krok24: dominanta se hledá JEN v okolí vybrané noty z ComboNoteName/
            // ComboOctave (±50 centů - s rezervou pokryje jiný komorní tón i
            // temperaturu i rozladění konkrétní píšťaly).
            int expectedNote = GetSelectedMidiNote();
            double expectedFreq = MidiNoteToFrequency(expectedNote);
            double lo = expectedFreq * Math.Pow(2.0, -50.0 / 1200.0);
            double hi = expectedFreq * Math.Pow(2.0, 50.0 / 1200.0);
            var inWindow = pikes.Where(p => p.Freq >= lo && p.Freq <= hi).ToList();

            if (inWindow.Count == 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "[VARHANY] V okolí noty {0}{1} ({2:F1} Hz ±50 centů) nebyl nalezen žádný pík.",
                    NoteNames[expectedNote % 12], expectedNote / 12 - 1, expectedFreq);

            var mainPeak = inWindow.OrderByDescending(p => p.Db).First();
            double fMax = mainPeak.Freq;

            // krok23: KAŽDÝ nalezený pík roztřídíme podle nejbližšího celého násobku
            // fMax. Co se do tolerance (v centech) nevejde, je ústřel (brum, vítr,
            // odraz v místnosti) a zahodí se. Frekvence alikvoty se bere PŘESNĚ
            // jako n * fMax (čistě harmonický model), ne naměřená syrová hodnota -
            // ta drobná odchylka je skoro jistě jen šum/rozladění, ne záměr.
            const double toleranceCents = 45.0;
            const int maxHarmonic = 24;

            var classified = new List<(int N, double Db)>();
            int rejected = 0;
            foreach (var p in pikes)
            {
                double ratio = p.Freq / fMax;
                int n = (int)Math.Round(ratio);
                if (n < 1 || n > maxHarmonic) { rejected++; continue; }

                double deviationCents = 1200.0 * Math.Log2(ratio / n);
                if (Math.Abs(deviationCents) > toleranceCents) { rejected++; continue; }

                classified.Add((n, p.Db));
            }

            if (classified.Count == 0)
                return "[VARHANY] Žádná alikvota se nepodařila spárovat s celočíselným násobkem dominanty.";

            // Víc píků na stejné harmonické (postranní lalok okna apod.) -> necháme jen nejsilnější.
            var best = classified
                .GroupBy(h => h.N)
                .Select(g => g.OrderByDescending(h => h.Db).First())
                .OrderBy(h => h.N)
                .ToList();

            double mainDb = best.First(h => h.N == 1).Db; // 1. harmonická = fMax, vždy přítomná

            var ratios = best.Select(h => (double)h.N).ToList();
            var relDbs = best.Select(h => h.Db - mainDb).ToList();
            var amps = relDbs.Select(db => Math.Pow(10.0, db / 20.0)).ToList();

            string ratiosLine = "PartialRatios    = new[] { " +
                string.Join(", ", ratios.Select(r => r.ToString("F2", CultureInfo.InvariantCulture))) + " },";
            string ampsLine = "PartialAmplitudes = new[] { " +
                string.Join(", ", amps.Select(a => a.ToString("F4", CultureInfo.InvariantCulture))) + " },";
            string dbLine = "// dB (info):        " +
                string.Join(" | ", relDbs.Select(db => db.ToString("F1", CultureInfo.InvariantCulture)));

            return string.Format(CultureInfo.InvariantCulture,
                "[VARHANY] DOMINANTA = {0:F2} Hz ({1:F1} dBFS) | vyřazeno jako ústřel: {2}\n",
                fMax, mainPeak.Db, rejected) +
                   ratiosLine + "\n" + ampsLine + "\n" + dbLine;
        }

        // krok23: MIDI nota -> frekvence (A4 = 440 Hz = MIDI 69).
        private static double MidiNoteToFrequency(int midiNote) => 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);

        // 🔔 KOUZLO 2: ZVONY (Přesná spektrální analýza inharmonických piků)
        private string AnalyzeBellPikes(double[] freqs, double[] dbs)
        {
            var pikes = FindAllPikes(freqs, dbs, -70.0);
            if (pikes.Count == 0) return "[ZVON] Žádný úder nenalezen.";

            var mainPeak = pikes.OrderByDescending(p => p.Db).First();
            double fMax = mainPeak.Freq;

            // Top 10 píků seřazených podle síly
            var topPikes = pikes.OrderByDescending(p => p.Db).Take(10).ToList();

            var lines = new System.Collections.Generic.List<string>();
            foreach (var p in topPikes)
            {
                double ratio = p.Freq / fMax;
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:F1}Hz ({1:F4}x | {2:F1}dB)", p.Freq, ratio, p.Db));
            }

            return string.Format(CultureInfo.InvariantCulture, "[ZVON] Hlavní pík: {0:F1} Hz\n", fMax) +
                   $"Inharmonická řada čár: " + string.Join(" | ", lines);
        }

        // 🥁 KOUZLO 3: ŠUMY A FUKY (Popis geometrie kopce pro ruční přepis)

        // 🥁 KOUZLO 3: ŠUMY A FUKY (Přepočet na kmitočty filtrů a sklon 20 dB/dekádu)
        private string AnalyzeNoiseShape(double[] freqs, double[] dbs)
        {
            // 1. Najdeme vrchol kopce (Střední kmitočet f0)
            int maxIdx = 0;
            double maxDb = -999;
            for (int i = 0; i < dbs.Length; i++)
            {
                if (freqs[i] >= 50 && dbs[i] > maxDb) // Ignorujeme brum pod 50 Hz
                {
                    maxDb = dbs[i];
                    maxIdx = i;
                }
            }

            if (maxDb < -75) return "[ŠUM] Žádný výrazný šumový profil nenalezen.";

            double f0 = freqs[maxIdx]; // Vrchol (střední kmitočet)

            // 2. Hledáme pokles o -3 dB (Mezní kmitočty f_low a f_high pro 3dB šířku pásma)
            double target3Db = maxDb - 3.0;

            double fLow = f0;
            for (int i = maxIdx; i >= 0; i--)
            {
                if (dbs[i] <= target3Db) { fLow = freqs[i]; break; }
            }

            double fHigh = f0;
            for (int i = maxIdx; i < dbs.Length; i++)
            {
                if (dbs[i] <= target3Db) { fHigh = freqs[i]; break; }
            }

            // Výpočet šířky pásma a činitele jakosti Q = f0 / Bandwidth
            double bandwidth = Math.Max(1.0, fHigh - fLow);
            double Q = f0 / bandwidth;

            // 3. Výpočet mezer pro sklon 20 dB / dekádu (pokles o 20 dB odpovídá faktoru 10x v kmitočtu)
            double target20Db = maxDb - 20.0;
            double fHp20dB = f0 / 10.0; // Teoretických 20dB/dekádu doleva
            double fLp20dB = f0 * 10.0; // Teoretických 20dB/dekádu doprava

            // Skutečně naměřené kmitočty při poklesu o -20 dB
            double fLow20 = f0;
            for (int i = maxIdx; i >= 0; i--)
            {
                if (dbs[i] <= target20Db) { fLow20 = freqs[i]; break; }
            }

            double fHigh20 = f0;
            for (int i = maxIdx; i < dbs.Length; i++)
            {
                if (dbs[i] <= target20Db) { fHigh20 = freqs[i]; break; }
            }

            return string.Format(CultureInfo.InvariantCulture,
                "[ŠUM / FILTR] Předpis pro filtr bílého šumu:\n" +
                "1. PÁSMOVÁ PROPUST (BPF 2.řád, 20dB/dek): Střed f0 = {0:F0} Hz | Jakost Q ≈ {1:F2}\n" +
                "2. KASKÁDA (HP + LP 20dB/dek): HP Cutoff (-20dB) = {2:F0} Hz | LP Cutoff (-20dB) = {3:F0} Hz",
                f0, Q, fLow20, fHigh20);
        }



        // Pomocná metoda pro nalezení všech lokálních píků (vrcholků) ve spektru.
        // krok23: frekvence i dB už nejsou "na bin přesně", ale doladěné parabolickou
        // interpolací přes 3 sousední body - u FFT 8192/44100 je to jinak jen ~5,4 Hz
        // na bin, což je na poměry alikvot hrubé.
        private System.Collections.Generic.List<(double Freq, double Db)> FindAllPikes(double[] freqs, double[] dbs, double minDbThreshold)
        {
            var list = new System.Collections.Generic.List<(double Freq, double Db)>();

            for (int i = 2; i < dbs.Length - 2; i++)
            {
                if (freqs[i] < 35) continue; // Ignorujeme brum pod 35 Hz

                // Lokální maximum (bod je vyšší než jeho 2 sousedi vlevo i vpravo)
                if (dbs[i] > minDbThreshold &&
                    dbs[i] > dbs[i - 1] && dbs[i] > dbs[i - 2] &&
                    dbs[i] > dbs[i + 1] && dbs[i] > dbs[i + 2])
                {
                    list.Add(ParabolicPeak(freqs, dbs, i));
                }
            }

            return list;
        }

        // krok23: kvadratická (parabolická) interpolace vrcholu přes bin i a jeho
        // dva sousedy - standardní trik na zpřesnění frekvence/amplitudy FFT píku
        // bez nutnosti většího okna. Pracuje přímo na dB hodnotách.
        private static (double Freq, double Db) ParabolicPeak(double[] freqs, double[] dbs, int i)
        {
            double y0 = dbs[i - 1], y1 = dbs[i], y2 = dbs[i + 1];
            double denom = y0 - 2.0 * y1 + y2;

            double p = Math.Abs(denom) > 1e-9 ? 0.5 * (y0 - y2) / denom : 0.0;
            p = Math.Clamp(p, -1.0, 1.0); // pojistka proti úletu u téměř plochého vrcholu

            double binWidth = freqs[i] - freqs[i - 1]; // rovnoměrná mřížka FFT, stačí jeden rozdíl
            double freq = freqs[i] + p * binWidth;
            double db = y1 - 0.25 * (y0 - y2) * p;

            return (freq, db);
        }












        private void BtnSnap_Click(object sender, RoutedEventArgs e)
        {
            _isFrozen = false;          // Odmrazíme graf
            _waitForSnap = true;        // Vyhodíme starý buffer
            _isMeasuringSnap = true;    // Nastavíme příznak pro audio vlákno

            BtnSnap.Content = "⏳ MĚŘÍM...";
        }








        private void LoadMicrophones()
        {
            ComboMicrophones.Items.Clear();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var capabilities = WaveIn.GetCapabilities(i);
                ComboMicrophones.Items.Add(capabilities.ProductName);
            }
            if (ComboMicrophones.Items.Count > 0)
                ComboMicrophones.SelectedIndex = 0;
        }

        private void InitFftOptions()
        {
            ComboFftSize.Items.Clear();
            ComboFftSize.Items.Add("8 192 (0,18 s - Fuk / Náběh, step 5,38 Hz)");
            ComboFftSize.Items.Add("16 384 (0,37 s - Rychlý náhled, step 2,69 Hz)");
            ComboFftSize.Items.Add("65 536 (1,48 s - Standard Břitva, step 0,67 Hz)");
            ComboFftSize.Items.Add("262 144 (5,93 s - Sub-Bass 16'/32', step 0,17 Hz)");

            ComboFftSize.SelectedIndex = 0;
        }

        private void ComboFftSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch (ComboFftSize.SelectedIndex)
            {
                case 0: _fftSize = 8192; break;
                case 1: _fftSize = 16384; break;
                case 2: _fftSize = 65536; break;
                case 3: _fftSize = 262144; break;
                default: _fftSize = 16384; break;
            }

            _sampleBuffer = new float[_fftSize];
            _bufferIndex = 0;
        }

        private void SetupPlot()
        {
            WpfPlot1.Plot.Title("Výpomocný frekvenční analyzátor");
            WpfPlot1.Plot.XLabel("Frekvence (Hz)");
            WpfPlot1.Plot.YLabel("Amplituda (dBFS)");

            double[] tickPositions = new double[] {
                Math.Log10(20), Math.Log10(50), Math.Log10(100), Math.Log10(200),
                Math.Log10(500), Math.Log10(1000), Math.Log10(2000), Math.Log10(5000), Math.Log10(10000)
            };

            string[] tickLabels = new string[] {
                "20 Hz", "50 Hz", "100 Hz", "200 Hz",
                "500 Hz", "1 kHz", "2 kHz", "5 kHz", "10 kHz"
            };

            WpfPlot1.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(tickPositions, tickLabels);
            WpfPlot1.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(10000), -90, 0);
            WpfPlot1.Refresh();
        }

        private void StartAudioCapture()
        {
            if (_waveIn != null) return;

            int selectedDevice = ComboMicrophones.SelectedIndex >= 0 ? ComboMicrophones.SelectedIndex : 0;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = selectedDevice,
                WaveFormat = new WaveFormat(SampleRate, 16, 1)
            };

            _waveIn.DataAvailable += OnAudioDataAvailable;
            _waveIn.StartRecording();
        }




        private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                float floatSample = sample / 32768.0f;

                float absSample = Math.Abs(floatSample);
                if (absSample > _maxPeak) _maxPeak = absSample;

                // Pokud jsme zmáčkli SNAP, okamžitě zahodíme stará data
                if (_waitForSnap)
                {
                    _bufferIndex = 0;
                    _waitForSnap = false;
                }

                if (_bufferIndex < _sampleBuffer.Length)
                {
                    _sampleBuffer[_bufferIndex] = floatSample;
                    _bufferIndex++;
                }

                // Máme naplněné celé nové okno!
                if (_bufferIndex >= _fftSize)
                {
                    _bufferIndex = 0;

                    // Bezpečně zjišťujeme stav z naší C# proměnné (žádné WPF UI!)
                    bool wasSnapCapture = _isMeasuringSnap;

                    ProcessFFT(_sampleBuffer, _maxPeak);
                    _maxPeak = 0;

                    // Pokud to byl odchyt pro SNAP, ihned zamkneme další překreslování
                    if (wasSnapCapture)
                    {
                        _isFrozen = true;
                        _isMeasuringSnap = false;

                        // Aktualizaci tlačítka pošleme bezpečně na UI vlákno
                        Dispatcher.Invoke(() => BtnSnap.Content = "📸 SNAP [Enter]");
                    }
                }
            }
        }



        private void ProcessFFT(float[] samples, float peak)
        {
            int n = samples.Length;
            double[] window = MathNet.Numerics.Window.Hann(n);

            Complex32[] buffer = new Complex32[n];
            for (int i = 0; i < n; i++)
            {
                float windowedSample = samples[i] * (float)window[i];
                buffer[i] = new Complex32(windowedSample, 0);
            }

            Fourier.Forward(buffer, FourierOptions.Matlab);

            int halfSize = n / 2;
            double[] freqsLog = new double[halfSize];
            double[] freqsHz = new double[halfSize];
            double[] magnitudesDb = new double[halfSize];

            for (int i = 0; i < halfSize; i++)
            {
                double freqHz = (i * (double)SampleRate) / n;
                freqsHz[i] = freqHz;
                freqsLog[i] = freqHz > 0 ? Math.Log10(freqHz) : 0;

                double mag = (buffer[i].Magnitude * 2.0) / n;
                magnitudesDb[i] = 20 * Math.Log10(Math.Max(mag, 1e-4));
            }

            double peakDb = 20 * Math.Log10(Math.Max(peak, 1e-4));
            double vuPercent = Math.Min(100, Math.Max(0, (peakDb + 60) * (100.0 / 60.0)));

            Dispatcher.Invoke(() =>
            {
                // 1. VU METR SE AKTUALIZUJE VŽDY
                VuMeter.Value = vuPercent;
                TxtVuDb.Text = peakDb.ToString("F1", CultureInfo.InvariantCulture) + " dB";

                if (peakDb >= -1.0)
                    VuMeter.Foreground = new SolidColorBrush(MediaColor.FromRgb(231, 76, 60));
                else if (peakDb >= -6.0)
                    VuMeter.Foreground = new SolidColorBrush(MediaColor.FromRgb(241, 196, 15));
                else
                    VuMeter.Foreground = new SolidColorBrush(MediaColor.FromRgb(46, 204, 113));

                // 2. GRAF A KOUZLA JEN KDYŽ NEJSME ZMRAZENI
                if (_isFrozen) return;

                // Vykreslení grafu
                WpfPlot1.Plot.Clear();
                var scatter = WpfPlot1.Plot.Add.Scatter(freqsLog, magnitudesDb);
                scatter.LineWidth = 1.5f;
                scatter.MarkerSize = 0;
                WpfPlot1.Refresh();

                // Výpočet a výpis Kouzla
                ApplyMagicAnalysis(freqsLog, magnitudesDb, freqsHz);
            });
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // ENTER nebo MEZERNÍK = SNAP (Zachytit)
            if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Space)
            {
                TriggerSnap();
                e.Handled = true; // Zamezí nechtěnému klikání na jiné prvky
            }
            // ESC = Zpět do Živého náhledu (Odmrazit)
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                TriggerLive();
                e.Handled = true;
            }
        }


        private void TriggerSnap()
        {
            _isFrozen = false;
            _waitForSnap = true;
            _isMeasuringSnap = true;
            BtnSnap.Content = "⏳ MĚŘÍM...";
        }

        private void TriggerLive()
        {
            _isFrozen = false;
            _isMeasuringSnap = false;
            BtnSnap.Content = "📸 SNAP [Enter]";
            TxtMagicOutput.Text = "Živý náhled spuštěn... Stiskni [Enter] pro zachycení okamžiku.";
        }


        private void BtnLive_Click(object sender, RoutedEventArgs e)
        {
            TriggerLive();
        }



    }
}