# PS150

Sada projektů okolo přehrávání a syntézy zvuku pro Windows, s výhledem na budoucí hardwarové
rozšíření (elektronické varhany na Raspberry Pi).

## Projekty v tomto řešení

- **PS150.UI.Windows** – přehrávač audio souborů (mp3, wav, flac, MIDI,), přehrávač video souborů (avi, MP4)
  napojení na MIDI-IN. Provizorně zatím používá SW knihovnu GM z windows 11 pro přehrávání souborů MIDI
- **PS150.UI.Linux** – přehrávač audio souborů (mp3, wav, flac, MIDI,), přehrávač video souborů (avi, MP4)
  napojení na MIDI-IN. plán do budouctna
- **PS150.UI.VideoEtAudio** – přehrávač audio souborů (mp3, wav, flac, MIDI,), přehrávač video souborů (avi, MP4)
  napojení na MIDI-IN. plán do budouctna, bude používat AVOLINIA, teda, až bude dostatečně vyzrálá.
- **PS150.Core** – SW syntezátor zvuku (oscilátory, obálky, filtry, hlasy nástrojů). Společné metody pro přehrávač souborů
- **PS150.Analyzer** – SW analyzátor zvuku s 2D grafy (WPF).
- **PS150.Raspi** – plánovaný hardwarový syntezátor (Raspberry Pi, HiFiBerry DAC8x, MIDI in/out, GPIO ovládání kláves).

## Stav projektu

⚠️ Projekt je ve **rané fázi vývoje**. Struktura i API se mohou často měnit, řada částí
je provizorní nebo nedokončená. Zpětná vazba a nápady vítány.

## Licence

Vlastní kód je licencován pod [MIT licencí](LICENSE).

# Third-Party Notices

This project (PS150) is licensed under the MIT License (see [LICENSE](LICENSE)),
but it also uses the following third-party libraries, which come with their own license terms.

---

## LibVLCSharp / LibVLCSharp.WPF (and LibVLC)

- **License:** GNU Lesser General Public License 2.1 or later (LGPL-2.1-or-later)
- **Source:** https://github.com/videolan/libvlcsharp
- **Note:** The library is referenced via dynamic linking (NuGet package / .dll reference),
  which satisfies the LGPL requirements. PS150's own code, which only consumes
  LibVLCSharp, remains under the MIT license.
  Full text of the LGPL 2.1 license: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html

## NAudio (NAudio, NAudio.Asio, NAudio.Midi, NAudio.Wasapi, NAudio.WinForms, NAudio.WinMM, NAudio.Core)

- **License:** MIT
- **Source:** https://github.com/naudio/NAudio

## Melanchall.DryWetMidi

- **License:** MIT
- **Source:** https://github.com/melanchall/drywetmidi

## System.Device.Gpio

- **License:** MIT
- **Source:** https://github.com/dotnet/iot

---

*If additional third-party libraries are added to the project, please list them here
along with their license and source.*

