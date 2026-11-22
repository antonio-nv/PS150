using System.Globalization;
using System.Runtime.InteropServices;
using PS150.Core.Filters;
using PS150.Core.Output;

// ======================================================================
// PS150.Asrock - zkušební (laboratorní) program pro ASRock ION 330 HT.
//
// Cíl: odladit na ASRocku věci, které poběží i na RP5: výhybku, mixér,
// syntezátor a příjem zvuku z PC po síti (virtuální externí zvukovka).
// Přehrávač to NENÍ (ten zůstává na PC).
//
// Přiřazení kanálů (jak je určil pokus se sluchátky):
//   0 výšky L   1 výšky P   2 hloubky L   3 hloubky P   4/5 střed (zatím rezerva)
//
// Režimy:
//   - Tón na jednom kanálu (A S D F G H) nebo na všech (V) - kontrola zapojení.
//   - Výhybka (X): zdroj (tón / šum / rozmítání) jde přes dvoupásmovou
//     výhybku Linkwitz-Riley 4. řádu: výšky na kanály 0,1, hloubky na 2,3.
//
// Ovládání:
//   A S D F G H   tón jen na jednom kanálu (A = kanál 0 ... H = kanál 5)
//   V             tón na všech kanálech (bez výhybky)
//   X             výhybka zapnout / vypnout
//   N             šum / sinus
//   W             rozmítání 50 Hz - 16 kHz za 20 s (zapnout / vypnout)
//   M             ticho
//   Nahoru / Dolů         kmitočet tónu o půltón (mimo rozmítání)
//   PageUp / PageDown     kmitočet tónu o oktávu
//   , a .         dělicí kmitočet výhybky o půltón dolů / nahoru
//   Doprava / Doleva      hlasitost o 3 dB (též + a -); nejvýš -12 dB,
//                         ať se nepoškodí uši ani reproduktory
//   Q nebo Esc            konec
//
// Parametry příkazové řádky (všechny volitelné):
//   --device surround51:0   název ALSA zařízení
//   --channels 6            počet kanálů
//   --rate 48000            vzorkovací kmitočet
//   --latency 50            latence v ms
//   --xover 2000            dělicí kmitočet výhybky v Hz
//   --auto                  bez klávesnice: projde postupně všechny kanály a skončí
//   --selftest              spočítá chování výhybky bez zvukové karty a skončí
// ======================================================================

// ---- NASTAVENÍ NATVRDO ----
const int ChannelHighL = 0;
const int ChannelHighR = 1;
const int ChannelLowL = 2;
const int ChannelLowR = 3;
const double DefaultCrossoverHz = 2000.0;   // dělicí kmitočet: změňte tady (nebo --xover)

string device = "surround51:0";
int channels = 6;
int sampleRate = 48000;
int latencyMs = 50;
double crossoverHz = DefaultCrossoverHz;
bool autoMode = false;
bool selfTest = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--device" when i + 1 < args.Length: device = args[++i]; break;
        case "--channels" when i + 1 < args.Length: channels = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--rate" when i + 1 < args.Length: sampleRate = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--latency" when i + 1 < args.Length: latencyMs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--xover" when i + 1 < args.Length: crossoverHz = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--auto": autoMode = true; break;
        case "--selftest": selfTest = true; break;
        default:
            Console.WriteLine($"Neznámý parametr: {args[i]}");
            return 1;
    }
}

Console.WriteLine($"PS150.Asrock   .NET {Environment.Version}   {RuntimeInformation.OSDescription}   {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Procesorů: {Environment.ProcessorCount}");

if (selfTest)
{
    RunSelfTest(sampleRate, crossoverHz);
    return 0;
}

string[] channelNames =
{
    "výšky L", "výšky P", "hloubky L", "hloubky P", "střed L (rez.)", "střed P (rez.)", "kanál 7", "kanál 8"
};

AlsaOutput output;
try
{
    output = new AlsaOutput(device, channels, sampleRate, latencyMs);
}
catch (Exception ex)
{
    Console.WriteLine($"Zvukovou kartu se nepodařilo otevřít: {ex.Message}");
    Console.WriteLine("Zkontrolujte, že zařízení nepoužívá jiný program (např. přehrávač), a zkuste --device hw:0,0");
    return 2;
}

Console.WriteLine($"Zařízení: {output.DeviceName}, {output.Channels} kanálů, {output.SampleRate} Hz, " +
                  $"latence {output.LatencyMs} ms, formát {output.FormatName}");

if (output.Channels <= ChannelLowR)
{
    Console.WriteLine("Pozor: karta má méně než 4 kanály, výhybka (kanály 0-3) nebude použitelná.");
}

// ---- sdílený stav mezi klávesnicí a zvukovým vláknem ----
var state = new ToneState
{
    FreqHz = 440.0,
    Gain = DbToGain(-30.0),
    Mask = 0,
    CrossoverHz = crossoverHz
};
bool running = true;
string? audioError = null;

Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

var audioThread = new Thread(() =>
{
    try
    {
        const int blockFrames = 256;
        int ch = output.Channels;
        float[] buffer = new float[blockFrames * ch];
        float[] currentGain = new float[ch];   // plynulé zesílení každého kanálu (bez lupanců)
        double phase = 0.0;
        double sweepPos = 0.0;                 // 0..1, poloha v rozmítání
        const double twoPi = 2.0 * Math.PI;
        var random = new Random(12345);

        var crossover = new StereoCrossover2Way(output.SampleRate, state.CrossoverHz);
        double appliedCrossoverHz = state.CrossoverHz;

        while (running)
        {
            float gainTarget = (float)state.Gain;
            int mask = state.Mask;
            bool useCrossover = state.Crossover;
            bool noise = state.Noise;
            bool sweep = state.Sweep;
            double increment = twoPi * state.FreqHz / output.SampleRate;

            double wantedCrossover = state.CrossoverHz;
            if (wantedCrossover != appliedCrossoverHz)
            {
                crossover.SetCrossover(wantedCrossover);
                appliedCrossoverHz = wantedCrossover;
            }

            for (int f = 0; f < blockFrames; f++)
            {
                float sample;
                if (noise)
                {
                    sample = (float)(random.NextDouble() * 2.0 - 1.0) * 0.7f;
                }
                else
                {
                    if (sweep)
                    {
                        // logaritmické rozmítání 50 Hz -> 16 kHz za 20 s, pak od začátku
                        double freq = 50.0 * Math.Pow(320.0, sweepPos);
                        increment = twoPi * freq / output.SampleRate;
                        sweepPos += 1.0 / (output.SampleRate * 20.0);
                        if (sweepPos >= 1.0) sweepPos -= 1.0;
                        state.SweepFreqHz = freq;
                    }

                    sample = (float)Math.Sin(phase);
                    phase += increment;
                    if (phase >= twoPi) phase -= twoPi;
                }

                float highL = 0f, highR = 0f, lowL = 0f, lowR = 0f;
                if (useCrossover)
                {
                    // zdroj je mono, takže levý i pravý vstup výhybky dostane totéž
                    crossover.ProcessFrame(sample, sample, out lowL, out lowR, out highL, out highR);
                }

                for (int c = 0; c < ch; c++)
                {
                    float target;
                    float value;

                    if (useCrossover)
                    {
                        switch (c)
                        {
                            case ChannelHighL: value = highL; target = gainTarget; break;
                            case ChannelHighR: value = highR; target = gainTarget; break;
                            case ChannelLowL: value = lowL; target = gainTarget; break;
                            case ChannelLowR: value = lowR; target = gainTarget; break;
                            default: value = 0f; target = 0f; break;
                        }
                    }
                    else
                    {
                        value = sample;
                        target = ((mask >> c) & 1) != 0 ? gainTarget : 0f;
                    }

                    currentGain[c] += (target - currentGain[c]) * 0.002f;
                    buffer[f * ch + c] = value * currentGain[c];
                }
            }

            output.Write(buffer);
        }
    }
    catch (Exception ex)
    {
        audioError = ex.Message;
        running = false;
    }
})
{
    IsBackground = true,
    Priority = ThreadPriority.Highest
};

audioThread.Start();

if (autoMode || Console.IsInputRedirected)
{
    // Bez klávesnice: každý kanál 1,5 s, potom výhybka s rozmítáním 6 s, pak konec.
    for (int c = 0; c < output.Channels && running; c++)
    {
        state.Mask = 1 << c;
        Console.WriteLine($"Kanál {c} ({NameOf(c)}) - {state.FreqHz:F1} Hz");
        Thread.Sleep(1500);
    }

    if (running)
    {
        Console.WriteLine($"Výhybka {state.CrossoverHz:F0} Hz: sinus 300 Hz (hloubky), pak 6 kHz (výšky)");
        state.Crossover = true;
        state.FreqHz = 300.0;
        Thread.Sleep(2000);
        state.FreqHz = 6000.0;
        Thread.Sleep(2000);
    }

    state.Mask = 0;
    state.Crossover = false;
    Thread.Sleep(300);
    running = false;
}
else
{
    Console.WriteLine();
    Console.WriteLine("A S D F G H = kanál 0..5, V = všechny, X = výhybka, N = šum, W = rozmítání, M = ticho");
    Console.WriteLine("Nahoru/Dolů = půltón, PgUp/PgDn = oktáva, , . = dělicí kmitočet, Doleva/Doprava = hlasitost, Q = konec");

    while (running)
    {
        PrintStatus();

        if (!Console.KeyAvailable)
        {
            Thread.Sleep(30);
            continue;
        }

        ConsoleKeyInfo key = Console.ReadKey(true);

        switch (key.Key)
        {
            case ConsoleKey.Q:
            case ConsoleKey.Escape:
                running = false;
                break;

            case ConsoleKey.M:
                state.Mask = 0;
                state.Crossover = false;
                break;

            case ConsoleKey.X:
                state.Crossover = !state.Crossover;
                break;

            case ConsoleKey.N:
                state.Noise = !state.Noise;
                break;

            case ConsoleKey.W:
                state.Sweep = !state.Sweep;
                break;

            case ConsoleKey.OemComma:
                state.CrossoverHz = Math.Max(state.CrossoverHz / Math.Pow(2.0, 1.0 / 12.0), 40.0);
                break;

            case ConsoleKey.OemPeriod:
                state.CrossoverHz = Math.Min(state.CrossoverHz * Math.Pow(2.0, 1.0 / 12.0), 16000.0);
                break;

            case ConsoleKey.UpArrow:
                state.FreqHz = Math.Min(state.FreqHz * Math.Pow(2.0, 1.0 / 12.0), 20000.0);
                break;

            case ConsoleKey.DownArrow:
                state.FreqHz = Math.Max(state.FreqHz / Math.Pow(2.0, 1.0 / 12.0), 20.0);
                break;

            case ConsoleKey.PageUp:
                state.FreqHz = Math.Min(state.FreqHz * 2.0, 20000.0);
                break;

            case ConsoleKey.PageDown:
                state.FreqHz = Math.Max(state.FreqHz / 2.0, 20.0);
                break;

            case ConsoleKey.RightArrow:
            case ConsoleKey.Add:
            case ConsoleKey.OemPlus:
                state.Gain = Math.Min(state.Gain * DbToGain(3.0), DbToGain(-12.0));
                break;

            case ConsoleKey.LeftArrow:
            case ConsoleKey.Subtract:
            case ConsoleKey.OemMinus:
                state.Gain = Math.Max(state.Gain * DbToGain(-3.0), DbToGain(-80.0));
                break;

            case ConsoleKey.V:
                state.Crossover = false;
                state.Mask = (1 << output.Channels) - 1;
                break;

            case ConsoleKey.A: SelectChannel(0); break;
            case ConsoleKey.S: SelectChannel(1); break;
            case ConsoleKey.D: SelectChannel(2); break;
            case ConsoleKey.F: SelectChannel(3); break;
            case ConsoleKey.G: SelectChannel(4); break;
            case ConsoleKey.H: SelectChannel(5); break;
            case ConsoleKey.F1: SelectChannel(0); break;
            case ConsoleKey.F2: SelectChannel(1); break;
            case ConsoleKey.F3: SelectChannel(2); break;
            case ConsoleKey.F4: SelectChannel(3); break;
            case ConsoleKey.F5: SelectChannel(4); break;
            case ConsoleKey.F6: SelectChannel(5); break;

            default:
                // Číslice (na české klávesnici jen se Shiftem, proto jsou navíc písmena výše).
                if (key.KeyChar == '0')
                {
                    state.Crossover = false;
                    state.Mask = (1 << output.Channels) - 1;
                }
                else if (key.KeyChar >= '1' && key.KeyChar <= '9')
                {
                    SelectChannel(key.KeyChar - '1');
                }
                break;
        }
    }

    Console.WriteLine();
}

running = false;
audioThread.Join(2000);

int underruns = output.UnderrunCount;
output.Dispose();

if (audioError != null)
{
    Console.WriteLine($"Chyba zvukového vlákna: {audioError}");
    return 3;
}

Console.WriteLine($"Hotovo. Přerušení zvuku (underrun) za celou dobu: {underruns}");
return 0;

// ---- pomocné funkce ----

void SelectChannel(int channel)
{
    if (channel >= output.Channels) return;
    state.Crossover = false;
    state.Mask = 1 << channel;
}

string NameOf(int channel) => channel < channelNames.Length ? channelNames[channel] : $"kanál {channel}";

void PrintStatus()
{
    string which;
    if (state.Crossover)
    {
        which = $"výhybka {state.CrossoverHz:F0} Hz";
    }
    else if (state.Mask == 0)
    {
        which = "ticho";
    }
    else if (state.Mask == (1 << output.Channels) - 1)
    {
        which = "všechny kanály";
    }
    else
    {
        int c = 0;
        while (((state.Mask >> c) & 1) == 0) c++;
        which = $"kanál {c} ({NameOf(c)})";
    }

    string source = state.Noise ? "šum" : (state.Sweep ? $"rozmítání {state.SweepFreqHz,7:F0} Hz" : $"sinus {state.FreqHz,7:F1} Hz");
    double db = 20.0 * Math.Log10(state.Gain);
    Console.Write($"\r  {which,-26} {source,-22} {db,6:F1} dB   underrun: {output.UnderrunCount}   ");
}

static double DbToGain(double db) => Math.Pow(10.0, db / 20.0);

// Samotest výhybky bez zvukové karty: pro několik kmitočtů pošle sinus výhybkou
// a vypíše úroveň hloubek, výšek a jejich součtu v dB vůči vstupu.
// Čekejte: v dělicím kmitočtu -6 dB u obou větví, součet kolem 0 dB všude.
static void RunSelfTest(int sampleRate, double crossoverHz)
{
    Console.WriteLine($"Samotest výhybky Linkwitz-Riley 4. řádu, dělicí kmitočet {crossoverHz:F0} Hz, {sampleRate} Hz");
    Console.WriteLine("  kmitočet     hloubky     výšky      součet");

    double[] frequencies = { 50, 100, 250, 500, 1000, 1500, 2000, 3000, 4000, 8000, 16000 };

    foreach (double freq in frequencies)
    {
        if (freq >= sampleRate / 2.0) continue;

        var xo = new LinkwitzRileyCrossover(sampleRate, crossoverHz);
        int total = sampleRate * 2;
        int skip = sampleRate / 2;     // úvodní rozběh filtrů nezapočítáváme
        double sumIn = 0, sumLow = 0, sumHigh = 0, sumBoth = 0;

        for (int n = 0; n < total; n++)
        {
            double x = Math.Sin(2.0 * Math.PI * freq * n / sampleRate);
            xo.Process(x, out double low, out double high);

            if (n < skip) continue;
            sumIn += x * x;
            sumLow += low * low;
            sumHigh += high * high;
            sumBoth += (low + high) * (low + high);
        }

        double lowDb = 10.0 * Math.Log10(Math.Max(sumLow / sumIn, 1e-20));
        double highDb = 10.0 * Math.Log10(Math.Max(sumHigh / sumIn, 1e-20));
        double bothDb = 10.0 * Math.Log10(Math.Max(sumBoth / sumIn, 1e-20));

        Console.WriteLine($"  {freq,7:F0} Hz  {lowDb,8:F2} dB {highDb,8:F2} dB {bothDb,8:F2} dB");
    }
}

// Stav sdílený mezi klávesnicí (hlavní vlákno) a zvukovým vláknem.
// Čtení a zápis double je na 64bitových systémech (x64, arm64) nedělitelné,
// takže tu nejsou potřeba zámky.
sealed class ToneState
{
    public volatile int Mask;
    public volatile bool Crossover;
    public volatile bool Noise;
    public volatile bool Sweep;
    public double FreqHz;
    public double SweepFreqHz;
    public double CrossoverHz;
    public double Gain;
}
