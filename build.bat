@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [FEHLER] .NET 8 SDK nicht gefunden.
    echo Download: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo === CyberMonitor wird gebaut ===
dotnet publish CyberMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "%~dp0publish"
if errorlevel 1 (
    echo [FEHLER] Build fehlgeschlagen.
    pause
    exit /b 1
)

echo.
echo Fertig:  %~dp0publish\CyberMonitor.exe
echo Tipp: Ordner "publish" an einen festen Ort kopieren (z.B. C:\Tools\CyberMonitor) und von dort starten.
echo Deinstallieren: Uninstall.bat im Programmordner (oder Tray-Menue "Deinstallieren ...").
pause
