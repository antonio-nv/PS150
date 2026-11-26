@echo off
rem Zkopiruje vysledek prekladu (Release) do C:\DOS\PS150.
rem - NEkopiruje settings.json (aby se neprepsalo nastaveni v C:\DOS\PS150)
rem - libvlc se zkopiruje jen kdyz v cili jeste neni
rem - PS150 musi byt zavreny (jinak jsou soubory zamcene)
set ZDROJ=C:\projekty_C\PS150\PS150.UI.Windows\bin\Release\net10.0-windows
set CIL=C:\DOS\PS150

if not exist "%CIL%" mkdir "%CIL%"

robocopy "%ZDROJ%" "%CIL%" /E /XD libvlc /XF settings.json *.pdb /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 goto chyba

if not exist "%CIL%\libvlc" (
  echo Kopiruji libvlc (poprve, trva chvili)...
  robocopy "%ZDROJ%\libvlc" "%CIL%\libvlc" /E /NJH /NJS /NP
  if %ERRORLEVEL% GEQ 8 goto chyba
)

echo.
echo Hotovo: %CIL%
goto konec

:chyba
echo.
echo CHYBA pri kopirovani (je PS150 zavreny?)

:konec
pause
