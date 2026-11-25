using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace CyberMonitor.Services;

/// <summary>
/// Autostart über die Windows-Aufgabenplanung (bei Anmeldung, "mit höchsten Privilegien").
/// Ein normaler Registry-Run-Eintrag würde bei einer Admin-App von der UAC blockiert –
/// die geplante Aufgabe startet dagegen ohne UAC-Abfrage.
/// </summary>
internal static class AutostartManager
{
    private const string TaskName = "CyberMonitor";

    public static bool Enable()
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Pfad der EXE unbekannt");
            string dir = Path.GetDirectoryName(exe) ?? "";
            string user = WindowsIdentity.GetCurrent().Name;

            string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>CyberMonitor Hardware-Overlay (Strg+Ö)</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Esc(user)}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Esc(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Esc(exe)}</Command>
      <Arguments>--autostart</Arguments>
      <WorkingDirectory>{Esc(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";

            string tmp = Path.Combine(Path.GetTempPath(), "cybermonitor_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* egal */ }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return false;
        }
    }

    public static bool Disable()
    {
        if (!IsEnabled()) return true;
        return RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;
    }

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    private static string Esc(string s) => SecurityElement.Escape(s) ?? s;

    private static int RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return -1;
        }
    }
}
