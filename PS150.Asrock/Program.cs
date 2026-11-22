using System.Globalization;
using System.Runtime.InteropServices;
using PS150.Core.Filters;
using PS150.Core.Input;
using PS150.Core.Network;
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
//   --xover 1000            dělicí kmitočet výhybky v Hz
//   --auto                  bez klávesnice: projde postupně všechny kanály a skončí
//   --selftest              spočítá chování výhybky bez zvukové karty a skončí
//   --nettest               nasimuluje přenos po síti (ztráty, kolísání, drift hodin) a skončí
//   --receive               VIRTUÁLNÍ ZVUKOVKA: přijímá zvuk z PC po síti, vede ho přes výhybku
//                           (výšky 0,1, hloubky 2,3) do karty a odpovídá na dotazy přehrávače
//   --port 5150             UDP port pro příjem (výchozí podle NetworkDefaults)
//   --delay 30              rezerva proti kolísání sítě v ms (kabel ~20-30, WiFi 60-100)
// ======================================================================

// ---- NASTAVENÍ NATVRDO ----
const int ChannelHighL = 0;
const int ChannelHighR = 1;
const int ChannelLowL = 2;
const int ChannelLowR = 3;
const double DefaultCrossoverHz = 1000.0;   // dělicí kmitočet: změňte tady (nebo --xover)

string device = "surround51:0";
int channels = 6;
int sampleRate = 48000;
int latencyMs = 50;
double crossoverHz = DefaultCrossoverHz;
bool autoMode = false;
bool selfTest = false;
bool netTest = false;
bool receiveMode = false;
int udpPort = PS150.Core.Network.NetworkDefaults.Port;
int delayMs = 30;

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
        case "--nettest": netTest = true; break;
        case "--receive": receiveMode = true; break;
        case "--port" when i + 1 < args.Length: udpPort = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--delay" when i + 1 < args.Length: delayMs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
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

if (netTest)
{
    RunNetTest(sampleRate);
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

if (receiveMode)
{
    return RunReceiver(output, crossoverHz, delayMs, udpPort);
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

// Virtuální externí zvukovka: zvuk z PC přichází po síti (UDP), jitter buffer ho
// srovná do pravidelného toku, výhybka ho rozdělí a jde do karty:
//   výšky L/P -> kanály 0,1   hloubky L/P -> kanály 2,3   (střed zatím nic)
// Hlasitost začíná na -20 dB, ať první zvuk z PC nepřekvapí zesilovače.
static int RunReceiver(AlsaOutput card, double startCrossoverHz, int targetDelayMs, int udpPort)
{
    int ch = card.Channels;
    if (ch <= ChannelLowR)
    {
        Console.WriteLine("Pro příjem je potřeba karta s alespoň 4 kanály.");
        card.Dispose();
        return 4;
    }

    var jitter = new AudioJitterBuffer(2, card.SampleRate, targetDelayMs);
    var info = new ReceiverInfo(card.SampleRate, 2, targetDelayMs + card.LatencyMs);
    var rx = new NetworkAudioInput(udpPort, jitter, info);

    try
    {
        rx.Start();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Nepodařilo se otevřít UDP port {udpPort}: {ex.Message}");
        card.Dispose();
        return 5;
    }

    Console.WriteLine($"Příjem na UDP portu {udpPort}, rezerva {targetDelayMs} ms, celkové zpoždění {info.LatencyMs} ms.");
    Console.WriteLine($"Výhybka {startCrossoverHz:F0} Hz. X = výhybka zap/vyp, , a . = dělicí kmitočet, Doleva/Doprava = hlasitost, Q = konec");

    var st = new ReceiverState { Crossover = true, CrossoverHz = startCrossoverHz, Gain = DbToGain(-20.0) };
    bool quit = false;
    string? failure = null;

    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit = true; };

    var worker = new Thread(() =>
    {
        try
        {
            const int block = 256;
            var inBuf = new float[block * 2];
            var outBuf = new float[block * ch];
            var xo = new StereoCrossover2Way(card.SampleRate, st.CrossoverHz);
            double applied = st.CrossoverHz;
            float gain = 0f;

            while (!quit)
            {
                jitter.Read(inBuf);   // když nic nepřichází, vrací ticho

                double wanted = st.CrossoverHz;
                if (wanted != applied)
                {
                    xo.SetCrossover(wanted);
                    applied = wanted;
                }

                float target = (float)st.Gain;
                bool useCrossover = st.Crossover;

                Array.Clear(outBuf, 0, outBuf.Length);

                for (int f = 0; f < block; f++)
                {
                    gain += (target - gain) * 0.002f;   // plynulá změna hlasitosti (bez lupanců)
                    float l = inBuf[f * 2] * gain;
                    float r = inBuf[f * 2 + 1] * gain;
                    int o = f * ch;

                    if (useCrossover)
                    {
                        xo.ProcessFrame(l, r, out float lowL, out float lowR, out float highL, out float highR);
                        outBuf[o + ChannelHighL] = highL;
                        outBuf[o + ChannelHighR] = highR;
                        outBuf[o + ChannelLowL] = lowL;
                        outBuf[o + ChannelLowR] = lowR;
                    }
                    else
                    {
                        outBuf[o + ChannelHighL] = l;
                        outBuf[o + ChannelHighR] = r;
                    }
                }

                card.Write(outBuf);   // blokuje tempem karty, tím se čte z bufferu pravidelně
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            quit = true;
        }
    })
    {
        IsBackground = true,
        Priority = ThreadPriority.Highest
    };

    worker.Start();

    bool interactive = !Console.IsInputRedirected;
    var statusClock = System.Diagnostics.Stopwatch.StartNew();

    while (!quit)
    {
        if (statusClock.ElapsedMilliseconds >= 250)
        {
            statusClock.Restart();
            string source = rx.LastAudioAgeMs < 500 ? "vysílá" : "ticho";
            double db = 20.0 * Math.Log10(Math.Max(st.Gain, 1e-6));
            string mode = st.Crossover ? $"výhybka {st.CrossoverHz:F0} Hz" : "bez výhybky";
            Console.Write($"\r  {source,-7} {mode,-18} zaplnění {jitter.FillMs,5:F1} ms  ztraceno {jitter.PacketsLost}  " +
                          $"pozdě {jitter.PacketsLate}  výpadky {jitter.Underruns}  tempo {jitter.RatePpm,5:F0} ppm  {db,5:F0} dB   ");
        }

        if (interactive && Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    quit = true;
                    break;

                case ConsoleKey.X:
                    st.Crossover = !st.Crossover;
                    break;

                case ConsoleKey.OemComma:
                    st.CrossoverHz = Math.Max(st.CrossoverHz / Math.Pow(2.0, 1.0 / 12.0), 40.0);
                    break;

                case ConsoleKey.OemPeriod:
                    st.CrossoverHz = Math.Min(st.CrossoverHz * Math.Pow(2.0, 1.0 / 12.0), 16000.0);
                    break;

                case ConsoleKey.RightArrow:
                case ConsoleKey.Add:
                case ConsoleKey.OemPlus:
                    st.Gain = Math.Min(st.Gain * DbToGain(2.0), 1.0);
                    break;

                case ConsoleKey.LeftArrow:
                case ConsoleKey.Subtract:
                case ConsoleKey.OemMinus:
                    st.Gain = Math.Max(st.Gain * DbToGain(-2.0), DbToGain(-80.0));
                    break;
            }
        }
        else
        {
            Thread.Sleep(30);
        }
    }

    Console.WriteLine();
    quit = true;
    worker.Join(2000);

    rx.Dispose();
    int cardUnderruns = card.UnderrunCount;
    card.Dispose();

    if (failure != null)
    {
        Console.WriteLine($"Chyba zvukového vlákna: {failure}");
        return 3;
    }

    Console.WriteLine($"Hotovo. Přijato paketů {rx.AudioPackets}, odpovědí na dotazy {rx.ProbesAnswered}, underrun karty {cardUnderruns}.");
    return 0;
}

// Simulace přenosu po síti bez sítě: odesílatel posílá sinus 1 kHz v paketech po
// 128 snímcích. Do cesty vložíme zpoždění s kolísáním (3-15 ms a občas špička 40 ms),
// 1 % ztracených paketů, přeházené pořadí a rozdíl hodin +100 ppm (odesílatel
// "jede" rychleji než zvuková karta). Příjemce čte po 256 snímcích v pravidelném
// tempu. Výsledek ukáže, jak se buffer s tím vyrovná.
static void RunNetTest(int sampleRate)
{
    const int channels = 2;
    const int packetFrames = 128;
    const int readFrames = 256;
    const double seconds = 60.0;
    const double lossProbability = 0.01;
    const int targetDelayMs = 30;

    var random = new Random(1);
    double senderRate = sampleRate * (1.0 + 100e-6);
    int packetCount = (int)(seconds * senderRate / packetFrames);

    var arrivals = new List<(double Time, AudioPacket Packet)>();
    var samples = new float[packetFrames * channels];
    var bytes = new byte[AudioPacket.EncodedSize(packetFrames, channels, AudioSampleFormat.Float32)];
    int sent = 0;
    int dropped = 0;

    for (int k = 0; k < packetCount; k++)
    {
        sent++;
        if (random.NextDouble() < lossProbability) { dropped++; continue; }

        long firstFrame = (long)k * packetFrames;
        for (int f = 0; f < packetFrames; f++)
        {
            float v = (float)(0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * (firstFrame + f) / sampleRate));
            samples[f * channels] = v;
            samples[f * channels + 1] = v;
        }

        int n = AudioPacket.Encode(bytes, 7u, (uint)k, firstFrame, sampleRate, channels, AudioSampleFormat.Float32, samples);
        if (!AudioPacket.TryDecode(bytes.AsSpan(0, n), out AudioPacket? packet) || packet == null)
        {
            Console.WriteLine("CHYBA: paket se nepodařilo dekódovat.");
            return;
        }

        double sendTime = k * packetFrames / senderRate;
        double delay = 0.003 + random.NextDouble() * 0.012 + (random.NextDouble() < 0.002 ? 0.04 : 0.0);
        arrivals.Add((sendTime + delay, packet));
    }

    arrivals.Sort((a, b) => a.Time.CompareTo(b.Time));

    var jitter = new AudioJitterBuffer(channels, sampleRate, targetDelayMs);
    var block = new float[readFrames * channels];
    int ticks = (int)(seconds * sampleRate / readFrames);
    int next = 0;
    long measured = 0, silent = 0;
    double minFill = double.MaxValue, maxFill = 0.0;

    for (int t = 0; t < ticks; t++)
    {
        double time = t * (double)readFrames / sampleRate;
        while (next < arrivals.Count && arrivals[next].Time <= time) jitter.Push(arrivals[next++].Packet);

        jitter.Read(block);

        if (time > 3.0)
        {
            double fill = jitter.FillMs;
            if (fill < minFill) minFill = fill;
            if (fill > maxFill) maxFill = fill;

            for (int f = 0; f < readFrames; f++)
            {
                measured++;
                if (block[f * channels] == 0f && block[f * channels + 1] == 0f) silent++;
            }
        }
    }

    Console.WriteLine($"Simulace přenosu: {seconds:F0} s, {sampleRate} Hz, cílové zpoždění {targetDelayMs} ms");
    Console.WriteLine($"  odesláno {sent} paketů, ztraceno na cestě {dropped} ({100.0 * dropped / sent:F2} %)");
    Console.WriteLine($"  příjemce: přijato {jitter.PacketsReceived}, hlášeno ztracených {jitter.PacketsLost}, " +
                      $"přeházených {jitter.PacketsReordered}, pozdních {jitter.PacketsLate}");
    Console.WriteLine($"  výpadky zvuku (underrun) {jitter.Underruns}, srovnání bufferu {jitter.Resyncs}");
    Console.WriteLine($"  zaplnění bufferu: {minFill:F1} až {maxFill:F1} ms, korekce tempa nakonec {jitter.RatePpm:F0} ppm");
    Console.WriteLine($"  tichých snímků na výstupu: {100.0 * silent / Math.Max(1, measured):F2} % (čekáme zhruba podíl ztracených paketů)");
}

// Stav sdílený mezi klávesnicí (hlavní vlákno) a zvukovým vláknem.
// Čtení a zápis double je na 64bitových systémech (x64, arm64) nedělitelné,
// takže tu nejsou potřeba zámky.
sealed class ReceiverState
{
    public volatile bool Crossover;
    public double CrossoverHz;
    public double Gain;
}

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
