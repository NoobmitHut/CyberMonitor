<#
    CyberMonitor - Deinstallation

    Entfernt alles, was CyberMonitor auf dem System hinterlaesst:
      1. laufende CyberMonitor-Prozesse
      2. die Autostart-Aufgabe "CyberMonitor" in der Aufgabenplanung
      3. eine evtl. nach einem Absturz uebrig gebliebene ETW-Session "CyberMonitor-FPS"
      4. einen evtl. uebrig gebliebenen Sensor-Treiberdienst von CyberMonitor
      5. Einstellungen und Logs in %APPDATA%\CyberMonitor
      6. den Programmordner (nur wenn er wirklich ein CyberMonitor-Installationsordner ist)

    Aufruf: Uninstall.bat doppelklicken, oder
      powershell -ExecutionPolicy Bypass -File Uninstall.ps1 [-Yes] [-KeepSettings] [-KeepFiles]

    Hinweis: Diese Datei enthaelt absichtlich keine Umlaute (Windows PowerShell 5.1 liest Skripte ohne BOM als ANSI).
#>
param(
    [switch]$Yes,                          # keine Rueckfrage
    [switch]$KeepSettings,                 # %APPDATA%\CyberMonitor behalten
    [switch]$KeepFiles,                    # Programmordner nicht loeschen
    [string]$UserAppData = $env:APPDATA,   # APPDATA des Benutzers (wird beim Hochstufen auf Admin weitergereicht)
    [int]$WaitPid = 0                      # vorher auf diesen Prozess warten (Aufruf aus dem Tray-Menue)
)

$ErrorActionPreference = 'Continue'
$TaskName   = 'CyberMonitor'
$EtwSession = 'CyberMonitor-FPS'
$AppDir     = Split-Path -Parent $PSCommandPath

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step([string]$text) { Write-Host "  > $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "    OK   $text" -ForegroundColor Green }
function Write-Skip([string]$text) { Write-Host "    --   $text" -ForegroundColor DarkGray }
function Write-Bad([string]$text)  { Write-Host "    FEHLER  $text" -ForegroundColor Red }

function Exit-Script([int]$code) {
    # Bei Fehlern immer offen lassen, damit man sie lesen kann.
    if (-not $Yes -or $code -ne 0) { [void](Read-Host "`nEnter zum Schliessen") } else { Start-Sleep -Seconds 3 }
    exit $code
}

# ---------------------------------------------------------------- Admin-Rechte holen
if (-not (Test-Admin)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                 '-UserAppData', "`"$UserAppData`"")
    if ($Yes)          { $argList += '-Yes' }
    if ($KeepSettings) { $argList += '-KeepSettings' }
    if ($KeepFiles)    { $argList += '-KeepFiles' }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -ErrorAction Stop
    } catch {
        Write-Host 'Abgebrochen: Fuer die Deinstallation werden Administratorrechte benoetigt.' -ForegroundColor Red
        Exit-Script 1
    }
    exit 0
}

# Nicht im Programmordner stehen bleiben, sonst laesst er sich nicht loeschen.
Set-Location -LiteralPath $env:TEMP

$Host.UI.RawUI.WindowTitle = 'CyberMonitor - Deinstallation'
Write-Host ''
Write-Host '  CYBER//MONITOR  ::  DEINSTALLATION' -ForegroundColor Magenta
Write-Host '  ----------------------------------' -ForegroundColor Magenta
Write-Host "  Programmordner : $AppDir"
Write-Host "  Einstellungen  : $(Join-Path $UserAppData 'CyberMonitor')"
Write-Host ''

if (-not $Yes) {
    $answer = Read-Host '  CyberMonitor wirklich entfernen? (j/n)'
    if ($answer -notmatch '^\s*[jy]') { Write-Host '  Abgebrochen.'; Exit-Script 0 }
    Write-Host ''
}

$failed = $false

# ---------------------------------------------------------------- 1. Prozesse beenden
Write-Step 'CyberMonitor beenden'
if ($WaitPid -gt 0) {
    Wait-Process -Id $WaitPid -Timeout 10 -ErrorAction SilentlyContinue
}
$procs = @(Get-Process -Name 'CyberMonitor' -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Write-Ok "$($procs.Count) Prozess(e) beendet"
} else {
    Write-Skip 'laeuft nicht'
}

# ---------------------------------------------------------------- 2. Autostart-Aufgabe
Write-Step 'Autostart-Aufgabe entfernen'
& schtasks.exe /Query /TN $TaskName *> $null
if ($LASTEXITCODE -eq 0) {
    & schtasks.exe /Delete /TN $TaskName /F *> $null
    if ($LASTEXITCODE -eq 0) { Write-Ok "Aufgabe '$TaskName' geloescht" } else { Write-Bad "Aufgabe '$TaskName' konnte nicht geloescht werden"; $failed = $true }
} else {
    Write-Skip 'nicht vorhanden'
}

# ---------------------------------------------------------------- 3. ETW-Session
Write-Step 'FPS-Messung (ETW-Session) beenden'
& logman.exe query $EtwSession -ets *> $null
if ($LASTEXITCODE -eq 0) {
    & logman.exe stop $EtwSession -ets *> $null
    if ($LASTEXITCODE -eq 0) { Write-Ok "Session '$EtwSession' beendet" } else { Write-Bad "Session '$EtwSession' konnte nicht beendet werden"; $failed = $true }
} else {
    Write-Skip 'nicht aktiv'
}

# ---------------------------------------------------------------- 4. Sensor-Treiber
# LibreHardwareMonitor entfernt seinen Treiberdienst normalerweise selbst. Nach einem Absturz kann er
# uebrig bleiben. Entfernt werden nur Dienste, die eindeutig zu CyberMonitor gehoeren - keine
# gemeinsam genutzten Treiber wie PawnIO oder WinRing0 anderer Programme.
Write-Step 'Sensor-Treiberdienst pruefen'
$drivers = @(Get-CimInstance -ClassName Win32_SystemDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*CyberMonitor*' -or ($_.PathName -and $_.PathName.IndexOf($AppDir, [StringComparison]::OrdinalIgnoreCase) -ge 0) })
if ($drivers.Count -gt 0) {
    foreach ($d in $drivers) {
        & sc.exe stop $d.Name *> $null
        & sc.exe delete $d.Name *> $null
        if ($LASTEXITCODE -eq 0) { Write-Ok "Dienst '$($d.Name)' entfernt" } else { Write-Bad "Dienst '$($d.Name)' konnte nicht entfernt werden"; $failed = $true }
    }
} else {
    Write-Skip 'keiner vorhanden'
}

# ---------------------------------------------------------------- 5. Einstellungen und Logs
Write-Step 'Einstellungen und Logs entfernen'
$cfg = Join-Path $UserAppData 'CyberMonitor'
if ($KeepSettings) {
    Write-Skip 'behalten (-KeepSettings)'
} elseif (Test-Path -LiteralPath $cfg) {
    Remove-Item -LiteralPath $cfg -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $cfg) { Write-Bad "$cfg konnte nicht geloescht werden"; $failed = $true } else { Write-Ok "$cfg geloescht" }
} else {
    Write-Skip 'nicht vorhanden'
}

# ---------------------------------------------------------------- 6. Programmordner
Write-Step 'Programmordner entfernen'
$isInstallDir =
    (Test-Path -LiteralPath (Join-Path $AppDir 'CyberMonitor.exe')) -and          # es ist wirklich CyberMonitor
    -not (Get-ChildItem -LiteralPath $AppDir -Filter '*.csproj' -ErrorAction SilentlyContinue) -and   # nicht der Quellcode-Ordner
    ([IO.Path]::GetPathRoot($AppDir).TrimEnd('\') -ne $AppDir.TrimEnd('\'))       # kein Laufwerks-Stammverzeichnis

$protected = @(
    [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('UserProfile'),
    [Environment]::GetFolderPath('MyDocuments'), [Environment]::GetFolderPath('ProgramFiles'),
    [Environment]::GetFolderPath('ProgramFilesX86'), [Environment]::GetFolderPath('Windows'),
    (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads')
) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') }
if ($protected -contains $AppDir.TrimEnd('\')) { $isInstallDir = $false }

if ($KeepFiles) {
    Write-Skip 'behalten (-KeepFiles)'
} elseif (-not $isInstallDir) {
    Write-Skip "uebersprungen - '$AppDir' ist kein eigener CyberMonitor-Ordner (bitte selbst aufraeumen)"
} else {
    for ($try = 1; $try -le 5 -and (Test-Path -LiteralPath $AppDir); $try++) {
        Remove-Item -LiteralPath $AppDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $AppDir) { Start-Sleep -Seconds 1 }
    }
    if (Test-Path -LiteralPath $AppDir) { Write-Bad "$AppDir konnte nicht (ganz) geloescht werden - Dateien noch in Benutzung?"; $failed = $true } else { Write-Ok "$AppDir geloescht" }
}

Write-Host ''
if ($failed) {
    Write-Host '  Deinstallation mit Fehlern beendet (siehe oben).' -ForegroundColor Yellow
    Exit-Script 1
}
Write-Host '  CyberMonitor wurde vollstaendig entfernt.' -ForegroundColor Green
Exit-Script 0
