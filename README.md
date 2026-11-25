# CYBER//MONITOR

Hardware-Overlay für Windows 10/11 im Cyberpunk-Look.
Mit **Strg + Ö** legst du es über jede Anwendung, ohne dass es den Fokus nimmt oder Klicks abfängt.

## Was angezeigt wird

| Bereich | Werte | Darstellung |
|---|---|---|
| **FPS** | FPS, Frametime, 1 % Low, Grafik-API | große Neon-Zahl, Verlaufsdiagramm (30 s) |
| **CPU** | Auslastung, Temperatur, Ø-Takt, höchster Kerntakt, Last pro Thread | 270°-Ring, LED-Temperaturbalken, Verlaufsdiagramm, Kern-Matrix |
| **GPU** | Auslastung, Temperatur, Kerntakt, Speichertakt | 270°-Ring, LED-Temperaturbalken, Verlaufsdiagramm |
| **Strom** | Summe CPU + GPU, CPU-Paket, Grafikkarte (Watt) | große Neon-Zahl, Verlaufsdiagramm |

Animationen: Einschalten im CRT-Stil mit Flackern, Glitch-Titel, Scanlines, Scan-Balken, blinkende LIVE-Anzeige
und weich animierte Ringe und Balken. Temperaturen werden ab 70 °C gelb und ab 85 °C rot.

## Vorschau

`Vorschau.html` im Browser öffnen. Dort ist das Overlay mit simulierten Werten über einer Spielszene
nachgebaut. Strg+Ö funktioniert auch dort. Die Datei ist nur zum Anschauen und gehört nicht zum Programm.

## Bauen (einmalig, unter Windows)

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installieren
2. `build.bat` doppelklicken
3. Den Ordner `publish\` an einen festen Ort kopieren, z. B. `C:\Tools\CyberMonitor\`
4. `CyberMonitor.exe` starten und die UAC-Abfrage bestätigen

Beim ersten Start legt das Programm den **Autostart** an. Danach startet es bei jeder Windows-Anmeldung
ohne UAC-Abfrage im Tray.

## Bedienung

- **Strg + Ö**: Overlay ein- oder ausblenden
- **Linksklick aufs Tray-Icon**: dasselbe
- **Rechtsklick aufs Tray-Icon**: Verschieben/Größe, angezeigte Werte, Autostart an/aus, Position, Overlay-Größe, Deinstallieren, Beenden
- **Strg + Shift + Ö**: Bearbeiten-Modus, um das Overlay mit der Maus zu verschieben und Breite/Höhe zu ändern (siehe unten).
  Funktioniert immer und blendet das Overlay bei Bedarf ein.
- **Strg + Shift + Plus / Minus** (Haupttastatur oder Nummernblock): Breite und Höhe gemeinsam um 10 % ändern.
  Diese Tasten sind nur belegt, solange das Overlay sichtbar ist.

### Größe und Position

Standard ist ein **kleines Rechteck oben links** (300 × 175).

**Mit der Maus:** **Strg + Shift + Ö** drücken (oder Tray → „Verschieben / Größe ändern“). Das Overlay bekommt einen
gelben Rahmen mit Griffen:

- **Fläche ziehen**: verschieben
- **Ecken ziehen**: Breite und Höhe ändern, die gegenüberliegende Ecke bleibt stehen
- **Kanten ziehen** (gelbe Striche in der Mitte jeder Seite): nur die Breite oder nur die Höhe ändern
- **Mausrad**: beides gemeinsam ändern
- **WERTE ▾** klicken: Auswahlmenü, in dem man jeden Wert einzeln ein- oder ausblendet
- **FERTIG** klicken oder Strg + Shift + Ö erneut drücken

In diesem Modus nimmt das Overlay Mausklicks an, bekommt aber nie den Tastaturfokus. Das Spiel behält also die Tastatur.
Danach lässt das Overlay Klicks wieder durch. Größe und Position werden gespeichert. Die Position merkt sich das
Overlay als „nächste Bildschirmecke + Abstand“. Beim Vergrößern bleibt es deshalb in seiner Ecke stehen.

**Der Inhalt passt sich dem Rechteck an:**

| Rechteck | Ansicht |
|---|---|
| klein (Standard) | Mini-Ansicht: FPS, CPU, GPU als Zeilen mit Frametime, Temperatur und Takt |
| flach und breit | Mini-Ansicht als Leiste nebeneinander |
| schmal und hoch | Mini-Ansicht nur mit den Hauptwerten |
| groß, hochkant | volle Ansicht mit Ringen, Verlaufsgrafiken und Kern-Matrix |
| groß, quer | volle Ansicht in 2 oder 3 Spalten |

Ist das Rechteck für die volle Ansicht etwas zu klein, fallen zuerst die Verlaufsgrafiken weg.

Im Tray-Menü unter **Overlay-Größe** gibt es feste Größen (Klein, Mittel, Groß, Breit, Leiste). Unter
**Overlay-Position** setzt man das Overlay in eine der vier Ecken zurück. Die Werte stehen auch in
`%APPDATA%\CyberMonitor\settings.json` (`OverlayWidth`, `OverlayHeight`, `Position`, `OffsetX`, `OffsetY`).

### Angezeigte Werte

Welche Werte das Overlay zeigt, stellt man im Bearbeiten-Modus über **WERTE ▾** ein, oder im Tray unter
**Angezeigte Werte**. Das Menü bleibt beim Anklicken offen, so kann man mehrere Werte nacheinander umschalten.

| Gruppe | Werte |
|---|---|
| FPS | FPS, Frametime, 1 % Low, Grafik-API |
| CPU | Auslastung, Temperatur, Takt, Last pro Kern (Matrix) |
| GPU | Auslastung, Temperatur, Takt, Speichertakt |
| Stromverbrauch | Summe CPU + GPU, CPU-Leistung, GPU-Leistung |
| Allgemein | Verlaufsgrafiken, Titelzeile und Uhr, Ziel und Status |

Sind alle Werte eines Bereichs aus, verschwindet der ganze Bereich (bzw. die Karte in der Mini-Ansicht), und der
übrige Inhalt bekommt mehr Platz. Mindestens ein Messwert bleibt immer sichtbar.

### Stromverbrauch

Angezeigt wird die Leistungsaufnahme des **CPU-Pakets** und der **Grafikkarte** in Watt sowie deren **Summe**.
Das ist der größte Teil des Verbrauchs, aber **nicht der Wert an der Steckdose**: Mainboard, RAM, Laufwerke, Lüfter
und die Verluste des Netzteils fehlen. Den echten Gesamtverbrauch kann Software nur mit einem Netzteil auslesen, das
seine Werte meldet (z. B. Corsair iCUE-Netzteile), oder mit einem Strommessgerät.

Das Overlay erscheint auf dem Monitor der gerade aktiven Anwendung. Die FPS werden immer für das
Programm im Vordergrund gemessen, der Prozessname steht als `TARGET` oben im Overlay.

## Ressourcenverbrauch

| Zustand | Was läuft |
|---|---|
| **Ausgeblendet** | Kein Sensor-Polling, keine ETW-Session, keine Timer, keine Animationen. Der Hardware-Treiber ist geschlossen. Windows weckt den Prozess nur, wenn du den Hotkey drückst. Zusätzlich ist der Windows-**Effizienzmodus** (EcoQoS) aktiv und der Arbeitsspeicher wird freigegeben. Die CPU-Last liegt bei 0 %. |
| **Eingeblendet** | Sensoren 1× pro Sekunde, FPS 2× pro Sekunde, Animationen auf 30 fps begrenzt, Mess-Threads mit niedriger Priorität |

Das Ein- und Ausschalten mit Strg+Ö öffnet und schließt die Sensoren jedes Mal komplett. Deshalb dauert es
nach dem Einblenden etwa eine Sekunde, bis die ersten Werte da sind (`SYNC SENSOR BUS ...`).

## Technik

- **WPF / .NET 8**, ein transparentes Fenster, das immer im Vordergrund liegt (`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`).
  Es lässt Klicks durch, nimmt nie den Fokus und erscheint nicht in Alt+Tab.
- **Hotkey** über `RegisterHotKey`. Windows fängt Strg+Ö ab, deshalb kommt die Kombination im Spiel nicht an.
- **Sensoren** über [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (NVIDIA, AMD, Intel)
- **FPS** über ETW-Present-Events (DXGI und D3D9), dasselbe Verfahren wie Microsofts PresentMon.
  Es wird nichts in das Spiel injiziert, deshalb gibt es keine Probleme mit Anti-Cheat.
  Gezählt wird nur die aktivste Swapchain des Spiels (Launcher- oder UI-Fenster desselben Prozesses fallen raus),
  reine Test-Presents (`DXGI_PRESENT_TEST`) werden ignoriert. Bei Store-/UWP-Spielen wird der eigentliche
  Spielprozess hinter `ApplicationFrameHost.exe` gemessen. **1 % Low** ist das 99. Perzentil der Frametimes
  der letzten 5 Sekunden.
- **Autostart** über die Windows-Aufgabenplanung („bei Anmeldung, mit höchsten Rechten“). Ein normaler
  Registry-Run-Eintrag funktioniert bei Programmen mit Admin-Rechten nicht.

## Bekannte Einschränkungen

- **Exklusiver Vollbildmodus:** Kein Overlay-Fenster (auch nicht Discord oder Steam ohne Hook) kann darüber
  gezeichnet werden. Im Spiel **„Randloses Fenster“ / „Borderless“** wählen. Unter Windows 10/11 klappt es dank
  „Vollbildoptimierungen“ aber meistens auch im Vollbild.
- **FPS bei Vulkan/OpenGL:** Die werden nur erfasst, wenn der Treiber über DXGI präsentiert. Bei NVIDIA:
  Systemsteuerung → *Vulkan/OpenGL-Present-Methode* → „Auf DXGI-Swapchain bevorzugen“.
  DirectX 9/10/11/12 funktioniert direkt.
- **CPU-Temperatur zeigt „PAWNIO FEHLT“:** Die verwendete LibreHardwareMonitor-Version (0.9.6) liest CPU-Temperatur,
  -Takt und -Leistung über den Kernel-Treiber [PawnIO](https://pawnio.eu). Einmal installieren (Installer von der
  Webseite oder `winget install namazso.PawnIO`), danach CyberMonitor neu starten. Ohne den Treiber nimmt CyberMonitor
  den **CPU-Takt aus den Windows-Leistungszählern** (wie der Task-Manager: Basistakt × „% Processor Performance“).
  Temperatur und CPU-Leistung lassen sich ohne Treiber nicht zuverlässig lesen. Das Overlay sagt das dann ausdrücklich
  (Temperaturfeld, Statuszeile, einmaliger Tray-Hinweis) und zeigt nicht nur `--`.
- Wenn Strg+Ö schon von einem anderen Programm belegt ist, meldet das Tray-Icon das. Das Overlay lässt sich
  dann per Klick aufs Icon öffnen.

## Deinstallation

Es gibt zwei Wege, beide räumen dasselbe auf:

- **Tray-Menü → „Deinstallieren ...“** und bestätigen. CyberMonitor beendet sich, danach läuft das Skript.
- **`Uninstall.bat`** im Programmordner doppelklicken (fragt nach Admin-Rechten). Das klappt auch, wenn
  CyberMonitor gar nicht mehr startet.

Entfernt werden:

1. laufende CyberMonitor-Prozesse
2. die Autostart-Aufgabe `CyberMonitor` in der Aufgabenplanung
3. eine nach einem Absturz evtl. übrig gebliebene ETW-Session `CyberMonitor-FPS`
4. ein übrig gebliebener Sensor-Treiberdienst, aber nur wenn er eindeutig zu CyberMonitor gehört
   (gemeinsam genutzte Treiber wie PawnIO bleiben)
5. Einstellungen und Logs in `%APPDATA%\CyberMonitor\`
6. der Programmordner selbst. Zur Sicherheit nur, wenn darin `CyberMonitor.exe` liegt und es kein
   Quellcode-Ordner, kein Laufwerk und kein Ordner wie Desktop oder Downloads ist.

Optionen für die Kommandozeile: `Uninstall.bat -Yes` (ohne Rückfrage), `-KeepSettings` (Einstellungen behalten),
`-KeepFiles` (Programmordner behalten).

Von Hand geht es so (als Admin):

```bat
schtasks /Delete /TN CyberMonitor /F
logman stop CyberMonitor-FPS -ets
rmdir /s /q "%APPDATA%\CyberMonitor"
```

## Projektstruktur

```
CyberMonitor/
├─ App.xaml(.cs)            Start, Tray, Hotkey, Ruhemodus
├─ OverlayWindow.xaml(.cs)  Cyberpunk-UI, Animationen, Positionierung
├─ Native.cs                Win32-Interop
├─ Uninstall.ps1 / .bat     Deinstallations-Kit (wird neben die EXE kopiert)
├─ Controls/                ArcGauge, Sparkline, SegmentBar, CoreBars
└─ Services/                HardwareService, FpsService, HotkeyManager,
                            AutostartManager, TrayIcon, AppSettings, Metrics, CpuPerfClock, Uninstaller
```
