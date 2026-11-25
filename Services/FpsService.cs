using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace CyberMonitor.Services;

public readonly record struct FpsStats(double Fps, double FrameTimeMs, double Low1, string Api);

/// <summary>
/// Misst FPS der Vordergrund-Anwendung über ETW-Present-Events (DXGI = DirectX 10/11/12, D3D9).
/// Gleiches Prinzip wie Microsofts PresentMon – kein Hook/keine Injektion ins Spiel.
/// Die ETW-Session existiert nur, solange das Overlay sichtbar ist.
/// </summary>
public sealed class FpsService
{
    public const string SessionName = "CyberMonitor-FPS";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42;
    private const int DxgiPresentMpoStart = 55;
    private const int D3D9PresentStart = 1;
    private const uint DxgiPresentTest = 0x1; // DXGI_PRESENT_TEST: prüft nur, zeigt nichts an

    private const int Cap = 8192; // Zweierpotenz
    private readonly double[] _ts = new double[Cap];
    private readonly double[] _deltas = new double[Cap];
    private readonly object _lock = new();
    private int _head, _count;
    private double _lastDxgiTs = double.MinValue;
    private double _lastTs = double.MinValue;
    private long _lastWallTick;
    private string _api = "--";
    private int _targetPid;

    // Haupt-Swapchain des Ziels: die, die in der letzten Sekunde am meisten präsentiert hat.
    // Verhindert, dass Launcher-/UI-Fenster oder ein zweiter Bildschirm desselben Prozesses mitgezählt werden.
    private readonly Dictionary<ulong, int> _swapChainCounts = new();
    private ulong _swapChain;
    private double _swapChainWindowStart = double.MinValue;

    // Wird bei jedem Start/Stop erhöht. Events einer alten Session (anderer Zeitbezug!) werden verworfen.
    private int _generation;

    private TraceEventSession? _session;

    public bool IsRunning => _session != null;

    /// <returns>null bei Erfolg, sonst Fehlermeldung.</returns>
    public string? Start()
    {
        if (_session != null) return null;
        if (TraceEventSession.IsElevated() != true) return "Admin-Rechte fehlen";

        int gen;
        lock (_lock)
        {
            Reset();
            gen = ++_generation;
        }

        TraceEventSession? session = null;
        try
        {
            // "Create" schließt eine evtl. nach einem Absturz übrig gebliebene Session gleichen Namens.
            session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create)
            {
                StopOnDispose = true,
                BufferSizeMB = 4,
            };
            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgiPresentStart, DxgiPresentMpoStart } });
            session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { D3D9PresentStart } });

            var source = session.Source;
            // Der Manifest-Parser liefert die Nutzdaten (SwapChain, Flags).
            // Events, die er nicht auflösen kann, kommen ohne Nutzdaten über UnhandledEvents.
            var parser = new RegisteredTraceEventParser(source);
            parser.All += e => OnEvent(e, gen);
            source.UnhandledEvents += e => OnEvent(e, gen);

            var thread = new Thread(() =>
            {
                try { source.Process(); }
                catch (Exception ex) { Log.Write(ex); }
            })
            {
                IsBackground = true,
                Name = "CyberMonitor.EtwFps",
                Priority = ThreadPriority.BelowNormal,
            };
            _session = session;
            thread.Start();
            return null;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            try { session?.Dispose(); } catch { /* egal */ }
            _session = null;
            return ex.Message;
        }
    }

    public void Stop()
    {
        var s = _session;
        _session = null;
        lock (_lock)
        {
            _generation++;
            Reset();
        }
        if (s == null) return;
        try { s.Dispose(); } catch (Exception ex) { Log.Write(ex); }
    }

    public void SetTarget(int pid)
    {
        if (Volatile.Read(ref _targetPid) == pid) return;
        lock (_lock)
        {
            Reset();
            Volatile.Write(ref _targetPid, pid);
        }
    }

    private void Reset()
    {
        _head = 0;
        _count = 0;
        _lastDxgiTs = double.MinValue;
        _lastTs = double.MinValue;
        _api = "--";
        _swapChainCounts.Clear();
        _swapChain = 0;
        _swapChainWindowStart = double.MinValue;
    }

    private void OnEvent(TraceEvent e, int gen)
    {
        int id = (int)e.ID;
        Guid provider = e.ProviderGuid;
        bool dxgi = provider == DxgiProvider && (id == DxgiPresentStart || id == DxgiPresentMpoStart);
        bool d3d9 = provider == D3D9Provider && id == D3D9PresentStart;
        if (!dxgi && !d3d9) return;
        if (e.ProcessID != Volatile.Read(ref _targetPid)) return;

        bool hasPayload = TryReadPayload(e, out ulong swapChain, out uint flags);
        if (dxgi && hasPayload && (flags & DxgiPresentTest) != 0) return; // kein echtes Bild

        double ts = e.TimeStampRelativeMSec;
        lock (_lock)
        {
            if (gen != _generation || e.ProcessID != _targetPid) return;
            if (dxgi)
            {
                _lastDxgiTs = ts;
                _api = "DXGI";
            }
            else
            {
                // D3D9 über DXGI würde doppelt zählen
                if (ts - _lastDxgiTs < 1000) return;
                _api = "D3D9";
            }

            if (hasPayload && swapChain != 0 && !AcceptSwapChain(swapChain, ts)) return;

            if (!Insert(ts)) return;
            _lastWallTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Fügt einen Zeitstempel sortiert ein. ETW stellt Echtzeit-Events pro CPU-Puffer zu, daher können
    /// einzelne Frames leicht vertauscht ankommen. Doppelte Zustellungen werden verworfen.
    /// </summary>
    private bool Insert(double ts)
    {
        if (ts == _lastTs) return false;
        int i = _head;
        _ts[i] = ts;
        _head = (_head + 1) & (Cap - 1);
        if (_count < Cap) _count++;

        // Nach hinten durchreichen, bis die Reihenfolge stimmt (meist 0 Schritte).
        for (int k = 1; k < _count && k < 256; k++)
        {
            int prev = (i - 1 + Cap) & (Cap - 1);
            double p = _ts[prev];
            if (p < ts) break;
            if (p == ts)
            {
                // Duplikat: Einfügen rückgängig machen
                for (int j = i; j != ((_head - 1 + Cap) & (Cap - 1)); j = (j + 1) & (Cap - 1))
                    _ts[j] = _ts[(j + 1) & (Cap - 1)];
                _head = (_head - 1 + Cap) & (Cap - 1);
                _count--;
                return false;
            }
            _ts[i] = p;
            _ts[prev] = ts;
            i = prev;
        }
        _lastTs = Math.Max(_lastTs, ts);
        return true;
    }

    /// <summary>Zählt Presents pro Swapchain und wechselt einmal pro Sekunde auf die aktivste.</summary>
    private bool AcceptSwapChain(ulong swapChain, double ts)
    {
        _swapChainCounts.TryGetValue(swapChain, out int n);
        _swapChainCounts[swapChain] = n + 1;

        if (_swapChainWindowStart == double.MinValue) _swapChainWindowStart = ts;
        if (_swapChain == 0) _swapChain = swapChain;

        if (ts - _swapChainWindowStart >= 1000)
        {
            ulong best = _swapChain;
            int bestCount = -1;
            foreach (var kv in _swapChainCounts)
            {
                if (kv.Value > bestCount)
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            _swapChain = best;
            _swapChainCounts.Clear();
            _swapChainWindowStart = ts;
        }

        return swapChain == _swapChain;
    }

    /// <summary>Liest SwapChain-Zeiger und Flags aus dem Event (DXGI: pIDXGISwapChain/Flags, D3D9: pSwapchain/Flags).</summary>
    private static bool TryReadPayload(TraceEvent e, out ulong swapChain, out uint flags)
    {
        swapChain = 0;
        flags = 0;
        try
        {
            string[]? names = e.PayloadNames;
            if (names == null || names.Length == 0) return false;
            bool any = false;
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (name.Contains("SwapChain", StringComparison.OrdinalIgnoreCase))
                {
                    swapChain = ToU64(e.PayloadValue(i));
                    any = true;
                }
                else if (name.Equals("Flags", StringComparison.OrdinalIgnoreCase))
                {
                    flags = unchecked((uint)ToU64(e.PayloadValue(i)));
                    any = true;
                }
            }
            return any;
        }
        catch
        {
            return false;
        }
    }

    private static ulong ToU64(object? v) => v switch
    {
        ulong u => u,
        long l => unchecked((ulong)l),
        uint u => u,
        int i => unchecked((uint)i),
        ushort s => s,
        short s => unchecked((ushort)s),
        _ => 0,
    };

    private double At(int i) => _ts[(_head - 1 - i + Cap) & (Cap - 1)]; // i = 0 → neuester Frame

    public FpsStats GetStats()
    {
        lock (_lock)
        {
            // ETW liefert gepuffert (~1 s Latenz) → großzügige Stale-Grenze
            if (_count < 2 || Environment.TickCount64 - _lastWallTick > 2500)
                return new FpsStats(0, 0, 0, _api);

            // FPS: Frames der letzten Sekunde (gemessen an Event-Zeitstempeln, nicht an der Ankunftszeit)
            double newest = At(0);
            double oldest = newest;
            int n = 1;
            while (n < _count)
            {
                double t = At(n);
                if (newest - t > 1000) break;
                oldest = t;
                n++;
            }
            double span = newest - oldest;
            double fps = span > 0 ? (n - 1) * 1000.0 / span : 0;

            // Frametime: Mittel der Abstände im selben Fenster
            double frameTime = n > 1 ? span / (n - 1) : 0;

            // 1 % Low: 99. Perzentil der Frametimes der letzten 5 Sekunden (wie PresentMon/CapFrameX "P1")
            int m = 0;
            for (int i = 0; i + 1 < _count; i++)
            {
                double a = At(i), b = At(i + 1);
                if (newest - b > 5000) break;
                _deltas[m++] = a - b;
            }
            double low = 0;
            if (m >= 20)
            {
                Array.Sort(_deltas, 0, m);
                double worst = _deltas[Math.Min(m - 1, (int)Math.Ceiling(m * 0.99) - 1)];
                low = worst > 0 ? Math.Min(1000.0 / worst, fps) : 0;
            }

            return new FpsStats(fps, frameTime, low, _api);
        }
    }
}
