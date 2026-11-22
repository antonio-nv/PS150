using System.Globalization;
using System.Runtime.InteropServices;
using PS150.Core.Output;

// ======================================================================
// PS150.Asrock - zkušební (laboratorní) program pro ASRock ION 330 HT.
//
// Krok 1: kontrola celé cesty PC -> ASRock -> zvuková karta.
// Program vypíše údaje o systému a zahraje sinusový tón na vybraném
// kanálu zvukové karty (0-5), případně postupně na všech.
//
// Přiřazení kanálů (jak je určil pokus se sluchátky):
//   0 výšky L   1 výšky P   2 hloubky L   3 hloubky P   4/5 střed (rezerva)
//
// Ovládání:
//   A S D F G H   tón jen na jednom kanálu (A = kanál 0 ... H = kanál 5);
//                 totéž dělají číslice 1..6 a klávesy F1..F6
//   V             tón na všech kanálech najednou (též číslice 0)
//   M             ticho
//   Nahoru / Dolů         kmitočet o půltón
//   PageUp / PageDown     kmitočet o oktávu
//   Doprava / Doleva      hlasitost o 3 dB (též + a -); nejvýš -12 dB,
//                         ať se nepoškodí uši ani reproduktory
//   Q nebo Esc            konec
//
// Parametry příkazové řádky (všechny volitelné):
//   --device surround51:0   název ALSA zařízení
//   --channels 6            počet kanálů
//   --rate 48000            vzorkovací kmitočet
//   --latency 50            latence v ms
//   --auto                  bez klávesnice: projde postupně všechny kanály a skončí
// ======================================================================

string device = "surround51:0";
int channels = 6;
int sampleRate = 48000;
int latencyMs = 50;
bool autoMode = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--device" when i + 1 < args.Length: device = args[++i]; break;
        case "--channels" when i + 1 < args.Length: channels = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--rate" when i + 1 < args.Length: sampleRate = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--latency" when i + 1 < args.Length: latencyMs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--auto": autoMode = true; break;
        default:
            Console.WriteLine($"Neznámý parametr: {args[i]}");
            return 1;
    }
}

string[] channelNames =
{
    "výšky L", "výšky P", "hloubky L", "hloubky P", "střed L (rez.)", "střed P (rez.)", "kanál 7", "kanál 8"
};

Console.WriteLine($"PS150.Asrock   .NET {Environment.Version}   {RuntimeInformation.OSDescription}   {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Procesorů: {Environment.ProcessorCount}");

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
                  $"latence {output.LatencyMs} ms, formát {(output.UsesFloat ? "float 32" : "16 bitů")}");

// ---- sdílený stav mezi klávesnicí a zvukovým vláknem ----
var state = new ToneState { FreqHz = 440.0, Gain = DbToGain(-30.0), Mask = 0 };
bool running = true;
string? audioError = null;

Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

var audioThread = new Thread(() =>
{
    try
    {
        const int blockFrames = 256;
        float[] buffer = new float[blockFrames * output.Channels];
        float[] currentGain = new float[output.Channels];   // plynulé zesílení každého kanálu (bez lupanců)
        double phase = 0.0;
        const double twoPi = 2.0 * Math.PI;

        while (running)
        {
            double increment = twoPi * state.FreqHz / output.SampleRate;
            float gainTarget = (float)state.Gain;
            int mask = state.Mask;

            for (int f = 0; f < blockFrames; f++)
            {
                float sample = (float)Math.Sin(phase);
                phase += increment;
                if (phase >= twoPi) phase -= twoPi;

                for (int c = 0; c < output.Channels; c++)
                {
                    float target = ((mask >> c) & 1) != 0 ? gainTarget : 0f;
                    currentGain[c] += (target - currentGain[c]) * 0.002f;
                    buffer[f * output.Channels + c] = sample * currentGain[c];
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
    // Bez klávesnice: každý kanál 1,5 s, pak konec.
    for (int c = 0; c < output.Channels && running; c++)
    {
        state.Mask = 1 << c;
        Console.WriteLine($"Kanál {c} ({NameOf(c)}) - {state.FreqHz:F1} Hz");
        Thread.Sleep(1500);
    }
    state.Mask = 0;
    Thread.Sleep(300);
    running = false;
}
else
{
    Console.WriteLine();
    Console.WriteLine("A S D F G H = kanál 0..5, V = všechny, M = ticho, Nahoru/Dolů = půltón, PgUp/PgDn = oktáva, Doleva/Doprava = hlasitost, Q = konec");

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
    if (channel < output.Channels) state.Mask = 1 << channel;
}

string NameOf(int channel) => channel < channelNames.Length ? channelNames[channel] : $"kanál {channel}";

void PrintStatus()
{
    string which;
    if (state.Mask == 0) which = "ticho";
    else if (state.Mask == (1 << output.Channels) - 1) which = "všechny kanály";
    else
    {
        int c = 0;
        while (((state.Mask >> c) & 1) == 0) c++;
        which = $"kanál {c} ({NameOf(c)})";
    }

    double db = 20.0 * Math.Log10(state.Gain);
    Console.Write($"\r  {which,-28} {state.FreqHz,8:F1} Hz   {db,6:F1} dB   underrun: {output.UnderrunCount}   ");
}

static double DbToGain(double db) => Math.Pow(10.0, db / 20.0);

// Stav sdílený mezi klávesnicí (hlavní vlákno) a zvukovým vláknem.
// Čtení a zápis double je na 64bitových systémech (x64, arm64) nedělitelné,
// takže tu nejsou potřeba zámky.
sealed class ToneState
{
    public volatile int Mask;
    public double FreqHz;
    public double Gain;
}
