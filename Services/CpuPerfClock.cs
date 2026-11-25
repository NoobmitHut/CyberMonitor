using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace CyberMonitor.Services;

/// <summary>
/// CPU-Takt ohne Kernel-Treiber, über die Windows-Leistungszähler – dasselbe Verfahren wie der Task-Manager
/// („Geschwindigkeit“): Basistakt × „% Processor Performance“ (über 100 % = Boost).
/// Wird nur benutzt, wenn LibreHardwareMonitor keinen Takt liefert (z. B. weil der Treiber PawnIO fehlt).
/// </summary>
internal sealed class CpuPerfClock : IDisposable
{
    private const string Category = "Processor Information";
    private const string Counter = "% Processor Performance";

    private readonly float _baseMhz;
    private readonly PerformanceCounter _total;
    private readonly PerformanceCounter[] _threads;

    private CpuPerfClock(float baseMhz, PerformanceCounter total, PerformanceCounter[] threads)
    {
        _baseMhz = baseMhz;
        _total = total;
        _threads = threads;
    }

    /// <returns>null, wenn die Zähler auf diesem System nicht verfügbar sind.</returns>
    public static CpuPerfClock? TryCreate()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            float baseMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0;
            if (baseMhz <= 0) return null;

            // Englische Namen funktionieren mit .NET auch auf deutschem Windows.
            var instances = new PerformanceCounterCategory(Category).GetInstanceNames()
                .Where(n => !n.Contains("_Total", StringComparison.Ordinal))
                .ToArray();
            var total = new PerformanceCounter(Category, Counter, "_Total", readOnly: true);
            var threads = instances.Select(n => new PerformanceCounter(Category, Counter, n, readOnly: true)).ToArray();

            // Erster Wert ist immer 0 – einmal vorlesen, damit der nächste Aufruf echte Werte liefert.
            total.NextValue();
            foreach (var t in threads) t.NextValue();
            return new CpuPerfClock(baseMhz, total, threads);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
    }

    /// <summary>Ø-Takt und höchster Thread-Takt in MHz.</summary>
    public (float? Average, float? Peak) Read()
    {
        try
        {
            float avg = _total.NextValue() * _baseMhz / 100f;
            float peak = 0;
            foreach (var t in _threads) peak = Math.Max(peak, t.NextValue() * _baseMhz / 100f);
            return (avg > 0 ? avg : null, peak > 0 ? Math.Max(peak, avg) : null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return (null, null);
        }
    }

    public void Dispose()
    {
        _total.Dispose();
        foreach (var t in _threads) t.Dispose();
    }
}
