using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace PS150.Core
{
    public class DirectoryNavigator
    {
        private List<string> _playlist = new();
        private int _currentIndex = -1;

        public string? CurrentFile => (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            ? _playlist[_currentIndex]
            : null;

        public void LoadDirectory(string initialFilePath)
        {
            if (string.IsNullOrEmpty(initialFilePath) || !File.Exists(initialFilePath))
                return;

            string? folder = Path.GetDirectoryName(initialFilePath);
            if (folder == null) return;

            // Načteme všechny podporované soubory v aktuální složce
            _playlist = Directory.GetFiles(folder)
                .Where(f => IsSupportedExtension(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _currentIndex = _playlist.IndexOf(initialFilePath);
        }

        public string? GetNextFile()
        {
            if (_playlist.Count == 0) return null;

            string? lastValidFile = CurrentFile;
            int previousIndex = _currentIndex;
            _currentIndex++;

            // Pokud jsme dojeli na konec složky, zkusíme najít sousední složku
            if (_currentIndex >= _playlist.Count)
            {
                if (TryToNavigateToNeighborFolder(lastValidFile, next: true))
                {
                    _currentIndex = 0; // První soubor v nové složce
                }
                else
                {
                    // ŽÁDNÁ další složka s hratelnými soubory - tohle byl
                    // opravdu konec. Vrátit null, ať to volající pozná,
                    // místo aby dokola dostával furt ten samý poslední
                    // soubor (to dřív způsobovalo nekonečnou smyčku všude,
                    // kde volající čekal na null jako signál konce).
                    _currentIndex = previousIndex;
                    return null;
                }
            }

            return CurrentFile;
        }

        public string? GetPreviousFile()
        {
            if (_playlist.Count == 0) return null;

            string? lastValidFile = CurrentFile;
            int previousIndex = _currentIndex;
            _currentIndex--;

            // Pokud jsme vyskočili před začátek složky, zkusíme přejít do předchozí složky
            if (_currentIndex < 0)
            {
                if (TryToNavigateToNeighborFolder(lastValidFile, next: false))
                {
                    _currentIndex = _playlist.Count - 1; // Poslední soubor v předchozí složce
                }
                else
                {
                    // Stejná oprava jako v GetNextFile() výše.
                    _currentIndex = previousIndex;
                    return null;
                }
            }

            return CurrentFile;
        }

        private bool TryToNavigateToNeighborFolder(string? referenceFile, bool next)
        {
            if (string.IsNullOrEmpty(referenceFile)) return false;

            string? currentFolder = Path.GetDirectoryName(referenceFile);
            if (currentFolder == null) return false;

            DirectoryInfo? parentDir = Directory.GetParent(currentFolder);
            if (parentDir == null) return false;

            // Seznam všech podsložek v nadřazeném adresáři
            var subFolders = parentDir.GetDirectories()
                .OrderBy(d => d.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int currentFolderIndex = subFolders.FindIndex(d => d.FullName.Equals(currentFolder, StringComparison.OrdinalIgnoreCase));
            if (currentFolderIndex == -1) return false;

            int targetFolderIndex = next ? currentFolderIndex + 1 : currentFolderIndex - 1;

            // Hledáme nejbližší složku, která obsahuje nějaké hratelné soubory
            while (targetFolderIndex >= 0 && targetFolderIndex < subFolders.Count)
            {
                var targetFolder = subFolders[targetFolderIndex];
                var filesInTarget = Directory.GetFiles(targetFolder.FullName)
                    .Where(f => IsSupportedExtension(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (filesInTarget.Count > 0)
                {
                    _playlist = filesInTarget;
                    return true;
                }

                targetFolderIndex += next ? 1 : -1;
            }

            return false;
        }


        private static bool IsSupportedExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".mid" || ext == ".midi" || ext == ".kar"
                || ext == ".avi" || ext == ".mp4" || ext == ".mkv" || ext == ".wmv"
                || ext == ".mov" || ext == ".flv" || ext == ".webm" || ext == ".m4v"; // <--- Doplněny chybějící video formáty
        }
    }

    /// <summary>
    /// Sdílené rozlišení "je tenhle soubor video, nebo zvuk (audio/MIDI)?" -
    /// DirectoryNavigator výše záměrně míchá oba typy do jednoho seznamu
    /// (jedna společná složka = jeden playlist), takže při procházení přes
    /// PageUp/PageDown nebo automatickém doehrání skladby může navigace
    /// kdykoliv "přejet" z jednoho typu na druhý. VgaEngine a MainWindow
    /// tímhle poznají, kdy musí samy sebe ukončit a předat soubor druhé
    /// straně - viz App.xaml.cs/MediaLauncher, které tohle střídání řídí.
    /// </summary>
    public static class MediaKind
    {
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v"
        };

        public static bool IsVideo(string filePath) =>
            VideoExtensions.Contains(Path.GetExtension(filePath));
    }
}