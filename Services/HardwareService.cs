using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace CyberMonitor.Services;

public sealed class HardwareSnapshot
{
    /// <summary>Zu welchem Start() der Snapshot gehört. Alte Snapshots nach erneutem Einblenden werden verworfen.</summary>
    public int Generation { get; set; }

    public string CpuName { get; set; } = "CPU";
    public float? CpuLoad { get; set; }
    public float? CpuTemp { get; set; }
    public float? CpuClock { get; set; }
    public float? CpuClockMax { get; set; }
    public float[] CoreLoads { get; set; } = Array.Empty<float>();
    /// <summary>Leistungsaufnahme des CPU-Pakets in Watt (braucht wie die Temperatur den Ring0-Treiber).</summary>
    public float? CpuPower { get; set; }

    public string GpuName { get; set; } = "GPU";
    public float? GpuLoad { get; set; }
    public float? GpuTemp { get; set; }
    public float? GpuCoreClock { get; set; }
    public float? GpuMemClock { get; set; }
    /// <summary>Leistungsaufnahme der Grafikkarte in Watt.</summary>
    public float? GpuPower { get; set; }

    /// <summary>
    /// CPU + GPU in Watt. Das ist NICHT der Verbrauch an der Steckdose (Mainboard, RAM, Laufwerke, Lüfter und
    /// Netzteilverluste fehlen) – Software kann den nur mit einem Mess-Netzteil auslesen.
    /// </summary>
    public float? TotalPower => CpuPower is null && GpuPower is null ? null : (CpuPower ?? 0) + (GpuPower ?? 0);

    public string? Error { get; set; }

    /// <summary>
    /// Der Kernel-Treiber PawnIO fehlt. Neuere LibreHardwareMonitor-Versionen brauchen ihn für CPU-Temperatur,
    /// -Takt und -Leistung (die Auslastung kommt direkt von Windows und geht trotzdem).
    /// </summary>
    public bool SensorDriverMissing { get; set; }

    /// <summary>Der CPU-Takt stammt aus den Windows-Leistungszählern statt vom Sensor (Ersatz ohne Treiber).</summary>
    public bool CpuClockFromWindows { get; set; }
}

/// <summary>
/// Liest CPU/GPU-Sensoren über LibreHardwareMonitor – aber NUR solange das Overlay sichtbar ist.
/// Start() öffnet die Sensoren auf einem eigenen Thread, Stop() schließt sie wieder komplett
/// (Treiber wird entladen, kein Polling mehr im Hintergrund).
/// </summary>
public sealed class HardwareService
{
    private const int IntervalMs = 1000;
    private const int FirstSampleMs = 400;

    // "CPU Core #3" (ältere LHM-Versionen) bzw. "CPU Core #3 Thread #2" (neuere, mit SMT)
    private static readonly Regex CoreLoadName = new(@"^CPU Core #(\d+)(?: Thread #(\d+))?$", RegexOptions.Compiled);
    // "Core #3" bzw. "CPU Core #3" – aber nicht "Core #3 (Effective)", "Bus Speed", "Cores (Average)" usw.
    private static readonly Regex CoreClockName = new(@"^(?:CPU )?Core #\d+$", RegexOptions.Compiled);

    private readonly object _runGate = new();
    private CancellationTokenSource? _cts;
    private int _generation;

    public event Action<HardwareSnapshot>? SnapshotReady;

    /// <summary>Kennung des aktuellen Laufs (siehe <see cref="HardwareSnapshot.Generation"/>).</summary>
    public int Generation => Volatile.Read(ref _generation);

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        int gen = Interlocked.Increment(ref _generation);
        var thread = new Thread(() => Run(cts, gen))
        {
            IsBackground = true,
            Name = "CyberMonitor.HardwarePoll",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        Interlocked.Increment(ref _generation);
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* Lauf war bereits beendet */ }
    }

    private void Run(CancellationTokenSource cts, int gen)
    {
        var ct = cts.Token;
        // Ein evtl. noch schließender Vorgänger-Lauf muss erst fertig sein.
        lock (_runGate)
        {
            Computer? pc = null;
            CpuPerfClock? perfClock = null;
            try
            {
                if (ct.IsCancellationRequested) return;

                bool driverMissing = !IsPawnIoInstalled();
                pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
                pc.Open();
                if (ct.IsCancellationRequested) return;

                // Ersatz für den CPU-Takt, falls der Sensor ihn nicht liefert (vor der Vor-Messung anlegen,
                // damit der erste echte Wert schon beim ersten Snapshot da ist).
                perfClock = CpuPerfClock.TryCreate();

                IHardware? cpu = pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                IHardware? gpu = PickGpu(pc.Hardware.Where(IsGpu).ToList());
                if (cpu == null && gpu == null)
                    throw new InvalidOperationException("Keine CPU/GPU-Sensoren gefunden");

                // Last-Sensoren rechnen mit der Differenz zweier Messungen. Ohne Vor-Messung wäre der
                // erste Wert 0 % oder ein Zufallswert aus wenigen Millisekunden.
                cpu?.Update();
                gpu?.Update();
                if (ct.WaitHandle.WaitOne(FirstSampleMs)) return;

                while (!ct.IsCancellationRequested)
                {
                    var snap = new HardwareSnapshot { Generation = gen };
                    if (cpu != null) ReadCpu(cpu, snap);
                    if (gpu != null) ReadGpu(gpu, snap);

                    if (snap.CpuClock == null && perfClock != null)
                    {
                        (snap.CpuClock, snap.CpuClockMax) = perfClock.Read();
                        snap.CpuClockFromWindows = snap.CpuClock != null;
                    }
                    else
                    {
                        perfClock?.Read(); // Zähler aktuell halten, damit ein späterer Wechsel sofort stimmt
                    }
                    snap.SensorDriverMissing = driverMissing && snap.CpuTemp == null;

                    if (!ct.IsCancellationRequested) SnapshotReady?.Invoke(snap);
                    if (ct.WaitHandle.WaitOne(IntervalMs)) break;
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                if (!ct.IsCancellationRequested)
                    SnapshotReady?.Invoke(new HardwareSnapshot { Generation = gen, Error = ex.Message });
            }
            finally
            {
                try { pc?.Close(); } catch (Exception ex) { Log.Write(ex); }
                perfClock?.Dispose();
                cts.Dispose();
            }
        }
    }

    /// <summary>PawnIO registriert sich als Treiberdienst „PawnIO“.</summary>
    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch
        {
            return true; // im Zweifel keinen falschen Hinweis anzeigen
        }
    }

    private static bool IsGpu(IHardware h) =>
        h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>
    /// Bevorzugt die dedizierte Grafikkarte: zuerst nach eigenem VRAM (≥ 2 GB = dediziert),
    /// dann NVIDIA &gt; AMD &gt; Intel. So gewinnt z. B. eine Intel Arc gegen eine Radeon-iGPU im Ryzen.
    /// </summary>
    private static IHardware? PickGpu(List<IHardware> gpus)
    {
        if (gpus.Count == 0) return null;
        if (gpus.Count == 1) return gpus[0];
        foreach (var g in gpus)
        {
            try { g.Update(); } catch (Exception ex) { Log.Write(ex); }
        }
        return gpus
            .OrderByDescending(g => DedicatedMemoryMb(g) >= 2048)
            .ThenByDescending(g => g.HardwareType switch
            {
                HardwareType.GpuNvidia => 3,
                HardwareType.GpuAmd => 2,
                _ => 1,
            })
            .ThenByDescending(DedicatedMemoryMb)
            .First();
    }

    private static float DedicatedMemoryMb(IHardware g) =>
        g.Sensors
            .Where(s => s.SensorType == SensorType.SmallData &&
                        (s.Name == "GPU Memory Total" || s.Name == "D3D Dedicated Memory Total"))
            .Select(s => s.Value ?? 0f)
            .DefaultIfEmpty(0f)
            .Max();

    private static ISensor[] UpdateAndCollect(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
        return hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).ToArray();
    }

    private static void ReadCpu(IHardware cpu, HardwareSnapshot s)
    {
        var all = UpdateAndCollect(cpu);
        s.CpuName = cpu.Name;

        s.CpuLoad = Find(all, SensorType.Load, allowZero: true, "CPU Total");

        // Pro Thread, sauber nach Kern- und Thread-Nummer sortiert (Sensor-Reihenfolge ist nicht garantiert).
        s.CoreLoads = all
            .Where(x => x.SensorType == SensorType.Load)
            .Select(x => (Sensor: x, Match: CoreLoadName.Match(x.Name)))
            .Where(t => t.Match.Success)
            .OrderBy(t => int.Parse(t.Match.Groups[1].Value))
            .ThenBy(t => t.Match.Groups[2].Success ? int.Parse(t.Match.Groups[2].Value) : 0)
            .Select(t => Valid(t.Sensor.Value, allowZero: true) ? Math.Min(t.Sensor.Value!.Value, 100f) : 0f)
            .ToArray();

        // Falls "CPU Total" fehlt: Mittel der Threads
        if (s.CpuLoad == null && s.CoreLoads.Length > 0) s.CpuLoad = s.CoreLoads.Average();

        // AMD: "Core (Tdie)" ist die echte Temperatur, "Tctl" hat bei manchen Modellen einen Offset (+10/+27 °C).
        s.CpuTemp = Find(all, SensorType.Temperature, allowZero: false,
                        "CPU Package", "Core (Tdie)", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Max", "Core Average", "CPU Cores")
                    ?? MaxOf(all, SensorType.Temperature, maxPlausible: 125);

        var clocks = all
            .Where(x => x.SensorType == SensorType.Clock && CoreClockName.IsMatch(x.Name) &&
                        Valid(x.Value, allowZero: false) && x.Value < 10_000) // Ausreißer beim ersten Lesen
            .Select(x => x.Value!.Value)
            .ToArray();
        if (clocks.Length > 0)
        {
            s.CpuClock = clocks.Average();
            s.CpuClockMax = clocks.Max();
        }

        // Intel: "CPU Package", AMD: "Package". Ältere Versionen/Modelle: nur Kern-Leistung.
        s.CpuPower = Find(all, SensorType.Power, allowZero: false, "CPU Package", "Package", "CPU Cores", "Cores")
                     ?? MaxOf(all, SensorType.Power, maxPlausible: 1000);
    }

    private static void ReadGpu(IHardware gpu, HardwareSnapshot s)
    {
        var all = UpdateAndCollect(gpu);
        s.GpuName = gpu.Name;

        // Bewusst KEIN Rückgriff auf "irgendeinen" Load-Sensor: Das wäre oft "GPU Memory" (VRAM-Belegung)
        // oder "GPU Bus" und würde eine falsche Auslastung anzeigen.
        s.GpuLoad = Find(all, SensorType.Load, allowZero: true, "GPU Core", "D3D 3D")
                    ?? MaxOf(all.Where(x => x.Name.StartsWith("D3D ", StringComparison.Ordinal)).ToArray(),
                             SensorType.Load, maxPlausible: 100, allowZero: true);
        if (s.GpuLoad > 100) s.GpuLoad = 100;

        s.GpuTemp = Find(all, SensorType.Temperature, allowZero: false, "GPU Core", "GPU Hot Spot")
                    ?? MaxOf(all, SensorType.Temperature, maxPlausible: 125);

        s.GpuCoreClock = Find(all, SensorType.Clock, allowZero: false, "GPU Core", "GPU Shader");
        s.GpuMemClock = Find(all, SensorType.Clock, allowZero: false, "GPU Memory");

        // NVIDIA (neuere LHM): "GPU Package", ältere: "GPU Power"; AMD: "GPU Package"/"GPU PPT"; Intel: "GPU Power".
        // Fallback: größter plausibler Leistungssensor (Gesamtleistung ist immer der größte).
        s.GpuPower = Find(all, SensorType.Power, allowZero: false,
                         "GPU Package", "GPU Power", "GPU Board Power", "GPU PPT", "GPU Total", "GPU TBP")
                     ?? MaxOf(all, SensorType.Power, maxPlausible: 1500);
    }

    private static bool Valid(float? v, bool allowZero) =>
        v.HasValue && !float.IsNaN(v.Value) && !float.IsInfinity(v.Value) && (allowZero ? v.Value >= 0 : v.Value > 0);

    private static float? Find(ISensor[] sensors, SensorType type, bool allowZero, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var x in sensors)
            {
                if (x.SensorType == type && x.Name == name && Valid(x.Value, allowZero))
                    return x.Value;
            }
        }
        return null;
    }

    private static float? MaxOf(ISensor[] sensors, SensorType type, float maxPlausible, bool allowZero = false)
    {
        float? max = null;
        foreach (var x in sensors)
        {
            if (x.SensorType == type && Valid(x.Value, allowZero) && x.Value <= maxPlausible && (max == null || x.Value > max))
                max = x.Value;
        }
        return max;
    }
}
