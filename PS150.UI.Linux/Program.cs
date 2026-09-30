// PS150.UI.Linux - vstupní bod. Stejný vzor jako MediaLauncher.Launch v
// PS150.UI.Windows/App.xaml.cs: podle typu souboru se spustí buď
// VideoWindow (fullscreen), nebo AudioTextPlayer (terminál); jakmile
// jeden z nich nastaví HandoffFile, přepne se appka na druhý, dokud
// nedojdou soubory nebo uživatel neukončí (Esc/q).

using System;
using System.IO;
using PS150.Core;
using PS150.UI.Linux;

if (args.Length == 0)
{
    Console.WriteLine("Použití: dotnet run -- /cesta/k/souboru (mp3/wav/flac/mp4/avi/mkv...)");
    return 1;
}

string? currentFile = args[0];

if (!File.Exists(currentFile))
{
    Console.WriteLine($"Soubor neexistuje: {currentFile}");
    return 1;
}

var navigator = new DirectoryNavigator();
navigator.LoadDirectory(currentFile);

while (currentFile != null)
{
    if (MediaKind.IsVideo(currentFile))
    {
        using var video = new VideoWindow(navigator);
        video.Run(currentFile);
        currentFile = video.HandoffFile;
        if (video.QuitRequested) break;
    }
    else
    {
        using var audio = new AudioTextPlayer(navigator);
        audio.Run(currentFile);
        currentFile = audio.HandoffFile;
        if (audio.QuitRequested) break;
    }
}

Console.WriteLine();
Console.WriteLine("[PS150] Konec.");
return 0;
