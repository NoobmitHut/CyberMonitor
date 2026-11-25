using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CyberMonitor.Services;

public sealed class AppSettings
{
    public bool AutoStart { get; set; } = true;

    /// <summary>Format der Datei. Bei Version &lt; 2 (alte Skalierung) werden Größe und Position auf Standard gesetzt.</summary>
    public int Version { get; set; }

    public const int CurrentVersion = 2;

    /// <summary>TopRight | TopLeft | BottomRight | BottomLeft</summary>
    public string Position { get; set; } = "TopLeft";

    public const double DefaultOffset = 10;

    /// <summary>
    /// Abstand (in DIP, also DPI-unabhängig) von der Bildschirmecke aus <see cref="Position"/>.
    /// Wird beim Verschieben mit der Maus gesetzt; die Tray-Positionen setzen ihn auf <see cref="DefaultOffset"/> zurück.
    /// </summary>
    public double OffsetX { get; set; } = DefaultOffset;

    public double OffsetY { get; set; } = DefaultOffset;

    public bool FirstRunDone { get; set; }

    /// <summary>Ein-/ausgeblendete Werte (Schlüssel aus <see cref="Metrics"/>). Fehlt ein Schlüssel, ist der Wert sichtbar.</summary>
    public Dictionary<string, bool> Metrics { get; set; } = new();

    public bool IsShown(string metric) => !Metrics.TryGetValue(metric, out bool on) || on;

    public void SetShown(string metric, bool on) => Metrics[metric] = on;

    // Overlay-Rechteck in DIP (DPI-unabhängig). Standard: kleines Rechteck → Mini-Ansicht.
    public const double DefaultWidth = 300;
    public const double DefaultHeight = 175;
    public const double MinWidth = 150;
    public const double MinHeight = 70;
    public const double MaxSize = 4000;

    /// <summary>Feste Größen fürs Tray-Menü.</summary>
    public static readonly (string Label, double Width, double Height)[] SizePresets =
    {
        ("Klein (Standard)", DefaultWidth, DefaultHeight),
        ("Mittel", 420, 245),
        ("Groß – volle Ansicht", 460, 690),
        ("Breit – 3 Spalten", 1200, 330),
        ("Leiste", 830, 82),
    };

    private double _width = DefaultWidth;
    private double _height = DefaultHeight;

    /// <summary>Breite des Overlays in DIP (auch direkt in settings.json änderbar).</summary>
    public double OverlayWidth
    {
        get => _width;
        set => _width = Clean(value, MinWidth, DefaultWidth);
    }

    /// <summary>Höhe des Overlays in DIP (auch direkt in settings.json änderbar).</summary>
    public double OverlayHeight
    {
        get => _height;
        set => _height = Clean(value, MinHeight, DefaultHeight);
    }

    private static double Clean(double v, double min, double fallback) =>
        double.IsFinite(v) ? Math.Round(Math.Clamp(v, min, MaxSize)) : fallback;

    /// <summary>Alte Einstellungen (Version 1: Skalierungsfaktor, Standard oben rechts) auf das neue Format bringen.</summary>
    private void Migrate()
    {
        if (Version >= CurrentVersion) return;
        Position = "TopLeft";
        OffsetX = DefaultOffset;
        OffsetY = DefaultOffset;
        OverlayWidth = DefaultWidth;
        OverlayHeight = DefaultHeight;
        Version = CurrentVersion;
        Save();
    }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyberMonitor");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                s.Migrate();
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return new AppSettings { Version = CurrentVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                string file = Path.Combine(AppSettings.Folder, "error.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging darf nie crashen
        }
    }
}
