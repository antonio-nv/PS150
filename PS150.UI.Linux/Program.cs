using LibVLCSharp.Shared;
using PS150.Core;

// Vynutit UTF-8 na výstupu - viz minulá konverzace o háčcích/čárkách.
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("Použití: PS150.UI.Linux <cesta k souboru>");
    return;
}

string filePath = args[0];

if (!File.Exists(filePath))
{
    Console.WriteLine($"Soubor nenalezen: {filePath}");
    return;
}

var navigator = new DirectoryNavigator();
navigator.LoadDirectory(filePath);

string? currentFile = navigator.CurrentFile;
if (currentFile == null)
{
    Console.WriteLine("Soubor se nepodařilo zařadit do playlistu.");
    return;
}

Console.WriteLine($"Aktuální soubor: {currentFile}");

// --- Přehrávání přes LibVLC ---
// POZOR: na Linuxu musí být libvlc nainstalované v systému přes apt
// (na rozdíl od Windows, kde to obstará NuGet balíček
// VideoLAN.LibVLC.Windows). Pokud tenhle řádek spadne s chybou o
// nenalezené knihovně, běž v terminálu:
//   sudo apt update && sudo apt install vlc
// (samotný balíček "vlc" s sebou přinese i libvlc jako závislost).
LibVLCSharp.Shared.Core.Initialize();

using var libVLC = new LibVLC();
using var mediaPlayer = new MediaPlayer(libVLC);

bool finished = false;
mediaPlayer.EndReached += (s, e) => finished = true;

using (var media = new Media(libVLC, new Uri(currentFile)))
{
    mediaPlayer.Play(media);
}

Console.WriteLine("Přehrávám... (Enter = zastavit)");

// Play() je asynchronní (vrátí se hned) - vlastní vlákno na Enter, ať
// hlavní smyčka může mezitím dál vypisovat postup.
bool stopRequested = false;
var inputThread = new Thread(() =>
{
    Console.ReadLine();
    stopRequested = true;
});
inputThread.IsBackground = true;
inputThread.Start();

while (!finished && !stopRequested)
{
    TimeSpan current = TimeSpan.FromMilliseconds(Math.Max(mediaPlayer.Time, 0));
    TimeSpan total = TimeSpan.FromMilliseconds(Math.Max(mediaPlayer.Length, 0));
    Console.Write($"\r{current:mm\\:ss} / {total:mm\\:ss}   ");
    Thread.Sleep(500);
}

Console.WriteLine();
Console.WriteLine(finished ? "Skladba dohrála." : "Zastaveno uživatelem.");

mediaPlayer.Stop();
