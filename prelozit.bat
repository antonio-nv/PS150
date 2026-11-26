@echo off
rem Prelozi PS150 (Release) bez Visual Studia primo do C:\DOS\PS150.
rem Potrebuje .NET SDK (prikaz dotnet). settings.json a slozka sf2 zustanou nedotcene.
cd /d C:\projekty_C\PS150

rem Zavre bezici PS150, jinak jsou exe a dll zamcene.
taskkill /f /im PS150.UI.Windows.exe >nul 2>&1

dotnet build PS150.UI.Windows\PS150.UI.Windows.csproj -c Release -o C:\DOS\PS150
if %ERRORLEVEL% NEQ 0 (
  echo.
  echo CHYBA prekladu.
) else (
  echo.
  echo Hotovo: C:\DOS\PS150
)
pause
