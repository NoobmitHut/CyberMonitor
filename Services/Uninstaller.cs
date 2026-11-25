using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CyberMonitor.Services;

/// <summary>Startet die Deinstallation aus dem Tray-Menü heraus (Uninstall.ps1 liegt neben der EXE).</summary>
internal static class Uninstaller
{
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Uninstall.ps1");

    /// <summary>
    /// Startet Uninstall.ps1 in einem sichtbaren Konsolenfenster. Das Skript wartet, bis dieser Prozess
    /// beendet ist. Der Aufrufer muss sich danach selbst beenden.
    /// </summary>
    /// <returns>false, wenn das Skript fehlt oder nicht gestartet werden konnte.</returns>
    public static bool LaunchScript()
    {
        if (!File.Exists(ScriptPath)) return false;
        try
        {
            // Der Prozess läuft schon mit Admin-Rechten → das Skript erbt sie, keine zweite UAC-Abfrage.
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(), // damit der Programmordner nicht gesperrt ist
            };
            foreach (string arg in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath,
                "-Yes",
                "-WaitPid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "-UserAppData", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            })
            {
                psi.ArgumentList.Add(arg);
            }
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return false;
        }
    }

    /// <summary>Notlösung ohne Skript: Autostart-Aufgabe und Einstellungsordner entfernen.</summary>
    public static bool CleanupWithoutScript()
    {
        bool ok = AutostartManager.Disable();
        try
        {
            if (Directory.Exists(AppSettings.Folder)) Directory.Delete(AppSettings.Folder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            ok = false;
        }
        return ok;
    }
}
