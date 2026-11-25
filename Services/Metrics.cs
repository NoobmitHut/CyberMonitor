namespace CyberMonitor.Services;

/// <summary>
/// Alle Werte, die man im Overlay ein- und ausblenden kann (Bearbeiten-Modus → „WERTE“ oder Tray → „Angezeigte Werte“).
/// Der Schlüssel steht so in settings.json unter "Metrics".
/// </summary>
public static class Metrics
{
    public const string Fps = "Fps";
    public const string FrameTime = "FrameTime";
    public const string Low1 = "Low1";
    public const string Api = "Api";

    public const string CpuLoad = "CpuLoad";
    public const string CpuTemp = "CpuTemp";
    public const string CpuClock = "CpuClock";
    public const string CpuCores = "CpuCores";

    public const string GpuLoad = "GpuLoad";
    public const string GpuTemp = "GpuTemp";
    public const string GpuClock = "GpuClock";
    public const string GpuMemClock = "GpuMemClock";

    public const string CpuPower = "CpuPower";
    public const string GpuPower = "GpuPower";
    public const string TotalPower = "TotalPower";

    public const string Graphs = "Graphs";
    public const string Header = "Header";
    public const string Status = "Status";

    /// <summary>Werte, die auch in der Mini-Ansicht vorkommen. Mindestens einer davon muss eingeblendet bleiben.</summary>
    public static readonly string[] CoreValues =
    {
        Fps, FrameTime, Low1, CpuLoad, CpuTemp, CpuClock, GpuLoad, GpuTemp, GpuClock, TotalPower, CpuPower, GpuPower,
    };

    /// <summary>Für die Menüs: Gruppen mit ihren Einträgen, in Anzeigereihenfolge.</summary>
    public static readonly (string Group, (string Key, string Label)[] Items)[] Catalog =
    {
        ("FPS", new[]
        {
            (Fps, "FPS"),
            (FrameTime, "Frametime"),
            (Low1, "1 % Low"),
            (Api, "Grafik-API"),
        }),
        ("CPU", new[]
        {
            (CpuLoad, "CPU-Auslastung"),
            (CpuTemp, "CPU-Temperatur"),
            (CpuClock, "CPU-Takt"),
            (CpuCores, "Last pro Kern (Matrix)"),
        }),
        ("GPU", new[]
        {
            (GpuLoad, "GPU-Auslastung"),
            (GpuTemp, "GPU-Temperatur"),
            (GpuClock, "GPU-Takt"),
            (GpuMemClock, "GPU-Speichertakt"),
        }),
        ("Stromverbrauch", new[]
        {
            (TotalPower, "Summe CPU + GPU (W)"),
            (CpuPower, "CPU-Leistung (W)"),
            (GpuPower, "GPU-Leistung (W)"),
        }),
        ("Allgemein", new[]
        {
            (Graphs, "Verlaufsgrafiken"),
            (Header, "Titelzeile und Uhr"),
            (Status, "Ziel und Status"),
        }),
    };
}
