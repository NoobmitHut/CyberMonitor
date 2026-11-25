@echo off
rem CyberMonitor deinstallieren (fragt nach Admin-Rechten). Optionen: -Yes -KeepSettings -KeepFiles
cd /d "%TEMP%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %* & exit /b
