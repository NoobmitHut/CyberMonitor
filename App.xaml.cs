using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyberMonitor.Services;

namespace CyberMonitor;

public partial class App : Application
{
    private const double ScaleStep = 0.1;

    private Mutex? _mutex;
    private HotkeyManager? _hotkey;
    private int[] _sizeHotkeys = Array.Empty<int>();
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private AppSettings _settings = new();
    private readonly HardwareService _hw = new();
    private readonly FpsService _fps = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\CyberMonitor.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // Alle Animationen auf 30 fps begrenzen → weniger GPU/CPU-Last, während das Overlay sichtbar ist.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
            new FrameworkPropertyMetadata { DefaultValue = 30 });

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write(ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Write(ex.ExceptionObject as Exception);
            // ETW-Sessions überleben den Prozess – beim Absturz wenigstens versuchen, sie zu schließen.
            try { _fps.Stop(); } catch { /* egal */ }
        };

        _settings = AppSettings.Load();
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            _settings.AutoStart = true; // Standard: mit Windows starten
            _settings.Save();
        }

        _overlay = new OverlayWindow(_hw, _fps, _settings);
        _tray = new TrayIcon(_settings, ToggleOverlay, SetAutostart, SetPosition, SetSize, ToggleEditMode, ToggleMetric,
                             Uninstall, ExitApp);
        _overlay.EditModeChanged += () =>
        {
            _tray?.SetEditModeChecked(_overlay.IsEditMode);
            if (!_overlay.IsEditMode) _tray?.CloseMetricsMenu();
        };
        _overlay.MetricsMenuRequested += () => _tray?.ShowMetricsMenu();
        bool driverHintShown = false;
        _overlay.SensorDriverMissing += () =>
        {
            if (driverHintShown) return; // einmal pro Programmstart reicht
            driverHintShown = true;
            _tray?.Balloon("CPU-Temperatur braucht PawnIO",
                "Für CPU-Temperatur und -Leistung fehlt der Treiber PawnIO (pawnio.eu oder: winget install namazso.PawnIO). " +
                "Danach CyberMonitor neu starten. Der CPU-Takt kommt solange von Windows.", warning: true);
        };
        // Mit der Maus verschoben/skaliert → speichern und Tray-Häkchen nachziehen
        _overlay.LayoutCommitted += () =>
        {
            _settings.Save();
            _tray?.SetPositionChecked(_settings.Position);
            _tray?.SetSizeChecked(_settings.OverlayWidth, _settings.OverlayHeight);
        };

        _hotkey = new HotkeyManager();
        bool hotkeyOk = _hotkey.RegisterCtrlChar('ö', ToggleOverlay) != 0;
        // Bearbeiten-Modus: immer verfügbar (blendet das Overlay bei Bedarf ein)
        _hotkey.RegisterChar(Native.MOD_CONTROL | Native.MOD_SHIFT, 'ö', ToggleEditMode);
        bool silent = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

        if (!hotkeyOk)
            _tray.Balloon("Hotkey belegt", "Strg+Ö wird schon von einem anderen Programm benutzt. Overlay per Klick aufs Tray-Icon öffnen.", warning: true);
        else if (!silent)
            _tray.Balloon("CyberMonitor aktiv", "Strg+Ö blendet das Overlay ein/aus. Im Hintergrund werden keine Daten gelesen.");

        // Autostart-Aufgabe (re)registrieren, damit sie immer auf den aktuellen EXE-Pfad zeigt.
        if (_settings.AutoStart)
            Task.Run(() => AutostartManager.Enable());

        Dispatcher.InvokeAsync(EnterIdle, DispatcherPriority.ApplicationIdle);
    }

    private void ToggleOverlay()
    {
        if (_overlay == null) return;
        if (_overlay.IsOverlayVisible)
        {
            UnregisterSizeHotkeys();
            _overlay.HideOverlay(EnterIdle);
        }
        else
        {
            Native.SetEfficiencyMode(false);
            _overlay.ShowOverlay();
            RegisterSizeHotkeys();
        }
    }

    /// <summary>
    /// Strg+Shift+Plus/Minus ändern die Größe. Die Tasten sind nur belegt, solange das Overlay sichtbar ist,
    /// damit sie anderen Programmen nicht dauerhaft weggenommen werden.
    /// </summary>
    private void RegisterSizeHotkeys()
    {
        if (_hotkey == null || _sizeHotkeys.Length > 0) return;
        const uint mods = Native.MOD_CONTROL | Native.MOD_SHIFT;
        _sizeHotkeys = new[]
        {
            _hotkey.Register(mods, Native.VK_OEM_PLUS, () => StepScale(+1)),
            _hotkey.Register(mods, Native.VK_ADD, () => StepScale(+1)),
            _hotkey.Register(mods, Native.VK_OEM_MINUS, () => StepScale(-1)),
            _hotkey.Register(mods, Native.VK_SUBTRACT, () => StepScale(-1)),
        };
    }

    /// <summary>Bearbeiten-Modus (verschieben/skalieren mit der Maus) ein/aus. Blendet das Overlay bei Bedarf ein.</summary>
    private void ToggleEditMode()
    {
        if (_overlay == null) return;
        if (_overlay.IsEditMode)
        {
            _overlay.EndEditMode();
            return;
        }
        if (!_overlay.IsOverlayVisible) ToggleOverlay();
        _overlay.BeginEditMode();
    }

    private void UnregisterSizeHotkeys()
    {
        foreach (int id in _sizeHotkeys) _hotkey?.Unregister(id);
        _sizeHotkeys = Array.Empty<int>();
    }

    /// <summary>Ruhemodus: Effizienzmodus an, Arbeitsspeicher freigeben. Danach wartet der Prozess nur noch auf den Hotkey.</summary>
    private static void EnterIdle()
    {
        Native.TrimMemory();
        Native.SetEfficiencyMode(true);
    }

    private void SetAutostart(bool enable)
    {
        bool ok = enable ? AutostartManager.Enable() : AutostartManager.Disable();
        if (ok)
        {
            _settings.AutoStart = enable;
            _settings.Save();
        }
        else
        {
            _tray?.Balloon("Autostart", "Die geplante Aufgabe konnte nicht geändert werden (Details in error.log).", warning: true);
        }
        _tray?.SetAutostartChecked(_settings.AutoStart);
    }

    private void SetPosition(string position)
    {
        _settings.Position = position;
        // Feste Ecke aus dem Tray-Menü → mit der Maus gesetzten Abstand verwerfen
        _settings.OffsetX = AppSettings.DefaultOffset;
        _settings.OffsetY = AppSettings.DefaultOffset;
        _settings.Save();
        _tray?.SetPositionChecked(position);
        if (_overlay?.IsOverlayVisible == true) _overlay.Reposition();
    }

    /// <summary>Wert im Overlay ein-/ausblenden (Auswahlmenü im Bearbeiten-Modus oder im Tray).</summary>
    private void ToggleMetric(string key)
    {
        bool show = !_settings.IsShown(key);
        if (!show && Array.IndexOf(Metrics.CoreValues, key) >= 0 &&
            Metrics.CoreValues.Count(k => k != key && _settings.IsShown(k)) == 0)
        {
            _tray?.Balloon("Angezeigte Werte", "Mindestens ein Messwert muss sichtbar bleiben.", warning: true);
            _tray?.RefreshMetricChecks();
            return;
        }
        _settings.SetShown(key, show);
        _settings.Save();
        _overlay?.ApplyMetrics();
        _tray?.RefreshMetricChecks();
    }

    /// <summary>Strg+Shift+Plus/Minus: Breite und Höhe gemeinsam um 10 % ändern.</summary>
    private void StepScale(int direction)
    {
        double f = direction > 0 ? 1 + ScaleStep : 1 / (1 + ScaleStep);
        SetSize(_settings.OverlayWidth * f, _settings.OverlayHeight * f);
    }

    private void SetSize(double width, double height)
    {
        _settings.OverlayWidth = width;   // wird auf Mindest-/Höchstmaß begrenzt
        _settings.OverlayHeight = height;
        _settings.Save();
        _tray?.SetSizeChecked(_settings.OverlayWidth, _settings.OverlayHeight);
        _overlay?.ApplySize();
    }

    private void Uninstall()
    {
        var answer = MessageBox.Show(
            "CyberMonitor komplett entfernen?\n\n" +
            "• Autostart-Aufgabe wird gelöscht\n" +
            "• Einstellungen und Logs (%APPDATA%\\CyberMonitor) werden gelöscht\n" +
            "• Der Programmordner wird gelöscht:\n   " + AppContext.BaseDirectory,
            "CyberMonitor deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        if (Uninstaller.LaunchScript())
        {
            // Das Skript wartet, bis dieser Prozess beendet ist, und räumt dann auf.
            ExitApp();
            return;
        }

        // Skript fehlt (z. B. nur die EXE kopiert): wenigstens Autostart und Einstellungen entfernen.
        bool ok = Uninstaller.CleanupWithoutScript();
        MessageBox.Show(
            (ok ? "Autostart und Einstellungen wurden entfernt." : "Nicht alles konnte entfernt werden (Details in error.log).") +
            "\n\nUninstall.ps1 wurde nicht gefunden. Bitte den Programmordner nach dem Beenden selbst löschen:\n" +
            AppContext.BaseDirectory,
            "CyberMonitor deinstallieren", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        ExitApp();
    }

    private void ExitApp()
    {
        _overlay?.HideImmediately();
        _hotkey?.Dispose();
        _hotkey = null;
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fps.Stop();
        _hw.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* nicht Besitzer */ }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
