using System;
using System.Collections.Generic;
using System.Windows.Interop;

namespace CyberMonitor.Services;

/// <summary>
/// Globale Hotkeys über RegisterHotKey auf einem unsichtbaren Message-Only-Fenster.
/// Kostet im Leerlauf 0 % CPU (Windows weckt den Thread nur beim Tastendruck).
/// Die Tastenkombinationen werden von Windows abgefangen und erreichen das Spiel nicht.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const uint VK_OEM_3 = 0xC0; // Position von "Ö" auf deutschem Layout
    private readonly HwndSource _sink;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 0x0C0D; // Anwendungen dürfen nur IDs 0x0000–0xBFFF benutzen

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("CyberMonitorHotkeySink")
        {
            ParentWindow = Native.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _sink = new HwndSource(p);
        _sink.AddHook(WndProc);
    }

    /// <summary>Registriert STRG + &lt;key&gt; (Standard: Ö). Gibt die Hotkey-ID zurück, 0 wenn belegt.</summary>
    public int RegisterCtrlChar(char key, Action callback) => RegisterChar(Native.MOD_CONTROL, key, callback);

    /// <summary>Registriert &lt;modifiers&gt; + &lt;key&gt;, die Taste wird über das aktuelle Tastaturlayout aufgelöst.</summary>
    public int RegisterChar(uint modifiers, char key, Action callback)
    {
        short scan = Native.VkKeyScanExW(key, Native.GetKeyboardLayout(0));
        uint vk = scan == -1 ? VK_OEM_3 : (uint)(scan & 0xFF);
        return Register(modifiers, vk, callback);
    }

    /// <summary>Registriert eine beliebige Kombination. Gibt die Hotkey-ID zurück, 0 wenn belegt.</summary>
    public int Register(uint modifiers, uint vk, Action callback)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(_sink.Handle, id, modifiers | Native.MOD_NOREPEAT, vk)) return 0;
        _callbacks[id] = callback;
        return id;
    }

    public void Unregister(int id)
    {
        if (id == 0 || !_callbacks.Remove(id)) return;
        Native.UnregisterHotKey(_sink.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            callback();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _callbacks.Keys) Native.UnregisterHotKey(_sink.Handle, id);
        _callbacks.Clear();
        _sink.RemoveHook(WndProc);
        _sink.Dispose();
    }
}
