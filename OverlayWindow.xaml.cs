using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyberMonitor.Controls;
using CyberMonitor.Services;

namespace CyberMonitor;

public partial class OverlayWindow : Window
{
    private enum State { Hidden, Showing, Visible, Hiding }

    private static readonly Brush HotBrush = ArcGauge.Brush(Color.FromRgb(0xFF, 0x2A, 0x4D), 0xFF);
    private static readonly Brush WarmBrush = ArcGauge.Brush(Color.FromRgb(0xFC, 0xEE, 0x0A), 0xFF);
    private static readonly Brush NormalBrush = ArcGauge.Brush(Color.FromRgb(0xEE, 0xF1, 0xFF), 0xFF);
    private static readonly Brush DimBrush = ArcGauge.Brush(Color.FromRgb(0x7C, 0x88, 0xAC), 0xFF);

    private readonly HardwareService _hw;
    private readonly FpsService _fps;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick;
    private readonly Random _rng = new();
    private readonly Dictionary<int, (string Name, long Time)> _procNames = new();
    private readonly int _ownPid = Environment.ProcessId;

    private IntPtr _hwnd;
    private State _state = State.Hidden;
    private int _tickCount;
    private bool _sensorsOnline;
    private string? _fpsError;

    public bool IsOverlayVisible => _state is State.Showing or State.Visible;

    public OverlayWindow(HardwareService hw, FpsService fps, AppSettings settings)
    {
        _hw = hw;
        _fps = fps;
        _settings = settings;
        InitializeComponent();

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => OnTick();

        SourceInitialized += OnSourceInitialized;
        Root.SizeChanged += (_, _) =>
        {
            UpdateFrameGeometry();
            ScheduleLayout();
        };
        SizeChanged += (_, _) => OnWindowSizeChanged();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Loaded);
        _hw.SnapshotReady += snap => Dispatcher.InvokeAsync(() => ApplySnapshot(snap));

        // Bearbeiten-Modus: Fläche ziehen = verschieben, Ecken/Kanten ziehen = Breite/Höhe, Mausrad = beides
        foreach (var handle in new FrameworkElement[] { GripTL, GripTR, GripBL, GripBR, EdgeT, EdgeB, EdgeL, EdgeR })
            handle.MouseLeftButtonDown += OnHandleDown;
        EditLayer.MouseLeftButtonDown += OnEditLayerDown;
        EditLayer.MouseMove += OnEditMove;
        EditLayer.MouseLeftButtonUp += (_, e) =>
        {
            if (EditLayer.IsMouseCaptured) EditLayer.ReleaseMouseCapture(); // → EndGesture()
            e.Handled = true;
        };
        EditLayer.LostMouseCapture += (_, _) => EndGesture();
        EditLayer.MouseWheel += OnEditWheel;
        // MouseDown hier abfangen, sonst würde der Klick auf FERTIG ein Verschieben starten und den MouseUp schlucken.
        DoneButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        DoneButton.MouseLeftButtonUp += (_, e) =>
        {
            EndEditMode();
            e.Handled = true;
        };
        MetricsButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        MetricsButton.MouseLeftButtonUp += (_, e) =>
        {
            MetricsMenuRequested?.Invoke();
            e.Handled = true;
        };

        InitMetricTables();
        ApplyMetrics();
        ApplySize();
    }

    /// <summary>Im Bearbeiten-Modus wurde „WERTE ▾“ geklickt → die App zeigt das Auswahlmenü an der Maus.</summary>
    public event Action? MetricsMenuRequested;

    /// <summary>Erstmals festgestellt, dass der Sensor-Treiber PawnIO fehlt (für einen einmaligen Tray-Hinweis).</summary>
    public event Action? SensorDriverMissing;

    private bool _driverMissing;

    // ------------------------------------------------------------------ Größe & Layout

    /// <summary>Größe, Ecke oder Abstand wurden mit der Maus geändert (Werte stehen schon in den Einstellungen) → speichern.</summary>
    public event Action? LayoutCommitted;

    public event Action? EditModeChanged;

    public bool IsEditMode { get; private set; }

    /// <summary>Übernimmt Breite/Höhe aus den Einstellungen. Die verankerte Ecke bleibt dabei stehen.</summary>
    public void ApplySize()
    {
        Width = _settings.OverlayWidth;
        Height = _settings.OverlayHeight;
        ShowSizeText(_settings.OverlayWidth, _settings.OverlayHeight);
    }

    private void ShowSizeText(double w, double h) =>
        EditSizeText.Text = w.ToString("0", CultureInfo.InvariantCulture) + " × " + h.ToString("0", CultureInfo.InvariantCulture);

    private enum View { Full, Mini }

    /// <param name="Detail">Full: 0 = alles, 1 = ohne Verlaufsgrafiken und Kern-Matrix.
    /// Mini: 0 = mit Nebenwerten (Frametime, Temperatur, Takt), 1 = schmal, nur Hauptwerte.</param>
    /// <param name="Header">nur Mini: Titelzeile anzeigen</param>
    private readonly record struct LayoutChoice(View View, int Detail, int Columns, bool Header);

    /// <summary>Eine Karte der Mini-Ansicht: Hauptwert links groß, Nebenwerte rechts klein.</summary>
    private sealed record MiniCardInfo(Border Card, TextBlock Main, string MainKey, FrameworkElement Sub, (TextBlock Text, string Key)[] Subs);

    /// <summary>Ein Bereich der vollen Ansicht und die Werte darin (Bereich verschwindet, wenn alle aus sind).</summary>
    private sealed record SectionInfo(FrameworkElement Section, string[] Keys);

    private MiniCardInfo[] _miniCards = Array.Empty<MiniCardInfo>();
    private SectionInfo[] _sections = Array.Empty<SectionInfo>();

    private const double SectionWidth = 380;
    private const double SectionGap = 14;
    private const double MiniCardWidth = 262;
    private const double MiniCardNarrowWidth = 146;

    private bool Shown(string metric) => _settings.IsShown(metric);

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static FrameworkElement Row(FrameworkElement valueText) => (FrameworkElement)valueText.Parent;

    private void InitMetricTables()
    {
        _sections = new[]
        {
            new SectionInfo(FpsSection, new[] { Metrics.Fps, Metrics.FrameTime, Metrics.Low1, Metrics.Api }),
            new SectionInfo(CpuSection, new[] { Metrics.CpuLoad, Metrics.CpuTemp, Metrics.CpuClock, Metrics.CpuCores }),
            new SectionInfo(GpuSection, new[] { Metrics.GpuLoad, Metrics.GpuTemp, Metrics.GpuClock, Metrics.GpuMemClock }),
            new SectionInfo(PowerSection, new[] { Metrics.TotalPower, Metrics.CpuPower, Metrics.GpuPower }),
        };
        _miniCards = new[]
        {
            new MiniCardInfo(MiniFpsCard, MiniFpsText, Metrics.Fps, MiniFpsSub,
                new[] { (MiniFrametimeText, Metrics.FrameTime), (MiniLowText, Metrics.Low1) }),
            new MiniCardInfo(MiniCpuCard, MiniCpuLoadText, Metrics.CpuLoad, MiniCpuSub,
                new[] { (MiniCpuTempText, Metrics.CpuTemp), (MiniCpuClockText, Metrics.CpuClock) }),
            new MiniCardInfo(MiniGpuCard, MiniGpuLoadText, Metrics.GpuLoad, MiniGpuSub,
                new[] { (MiniGpuTempText, Metrics.GpuTemp), (MiniGpuClockText, Metrics.GpuClock) }),
            new MiniCardInfo(MiniPwrCard, MiniTotalPowerText, Metrics.TotalPower, MiniPwrSub,
                new[] { (MiniCpuPowerText, Metrics.CpuPower), (MiniGpuPowerText, Metrics.GpuPower) }),
        };
    }

    /// <summary>
    /// Übernimmt die Auswahl „angezeigte Werte“ aus den Einstellungen und wählt danach das Layout neu
    /// (weniger Werte → mehr Platz für den Rest).
    /// </summary>
    public void ApplyMetrics()
    {
        // ---- volle Ansicht: einzelne Zeilen
        var fpsBig = Row(FpsText);
        fpsBig.Visibility = Vis(Shown(Metrics.Fps));
        ((Grid)fpsBig.Parent).ColumnDefinitions[0].Width = new GridLength(Shown(Metrics.Fps) ? 150 : 0);
        Row(FrametimeText).Visibility = Vis(Shown(Metrics.FrameTime));
        Row(LowText).Visibility = Vis(Shown(Metrics.Low1));
        Row(ApiText).Visibility = Vis(Shown(Metrics.Api));
        Row(Row(FrametimeText)).Visibility = Vis(Shown(Metrics.FrameTime) || Shown(Metrics.Low1) || Shown(Metrics.Api));

        var cpuGauge = (FrameworkElement)CpuGauge.Parent;
        cpuGauge.Visibility = Vis(Shown(Metrics.CpuLoad));
        ((Grid)cpuGauge.Parent).ColumnDefinitions[0].Width = new GridLength(Shown(Metrics.CpuLoad) ? 118 : 0);
        Row(CpuTempText).Visibility = Vis(Shown(Metrics.CpuTemp));
        CpuTempBar.Visibility = Vis(Shown(Metrics.CpuTemp));
        Row(CpuClockText).Visibility = Vis(Shown(Metrics.CpuClock));
        Row(CpuPeakText).Visibility = Vis(Shown(Metrics.CpuClock));

        var gpuGauge = (FrameworkElement)GpuGauge.Parent;
        gpuGauge.Visibility = Vis(Shown(Metrics.GpuLoad));
        ((Grid)gpuGauge.Parent).ColumnDefinitions[0].Width = new GridLength(Shown(Metrics.GpuLoad) ? 118 : 0);
        Row(GpuTempText).Visibility = Vis(Shown(Metrics.GpuTemp));
        GpuTempBar.Visibility = Vis(Shown(Metrics.GpuTemp));
        Row(GpuClockText).Visibility = Vis(Shown(Metrics.GpuClock));
        Row(GpuMemText).Visibility = Vis(Shown(Metrics.GpuMemClock));

        var pwrBig = Row(TotalPowerText);
        pwrBig.Visibility = Vis(Shown(Metrics.TotalPower));
        ((Grid)pwrBig.Parent).ColumnDefinitions[0].Width = new GridLength(Shown(Metrics.TotalPower) ? 150 : 0);
        Row(CpuPowerText).Visibility = Vis(Shown(Metrics.CpuPower));
        Row(GpuPowerText).Visibility = Vis(Shown(Metrics.GpuPower));

        // ---- Kopf- und Fußzeile
        bool header = Shown(Metrics.Header), status = Shown(Metrics.Status);
        ((FrameworkElement)TitleGroup.Parent).Visibility = Vis(header);
        TargetText.Visibility = Vis(status);
        StatusText.Visibility = Vis(status);
        FullHeader.Visibility = Vis(header || status);
        FullFooter.Visibility = Vis(header);

        // ---- Mini-Ansicht: Hauptwerte und Nebenwerte (welche Karte sichtbar ist, entscheidet ApplyLayout)
        foreach (var m in _miniCards)
        {
            m.Main.Visibility = Vis(Shown(m.MainKey));
            ((Grid)m.Main.Parent).ColumnDefinitions[1].Width = new GridLength(Shown(m.MainKey) ? 86 : 0);
            foreach (var (text, key) in m.Subs) text.Visibility = Vis(Shown(key));
        }

        if (IsLoaded && Root.ActualWidth > 0) ScheduleLayout();
    }

    private bool CardVisible(MiniCardInfo m, bool narrow) =>
        Shown(m.MainKey) || (!narrow && m.Subs.Any(s => Shown(s.Key)));

    /// <summary>
    /// Stufen von "viel Inhalt" bis "wenig Inhalt". Gewählt wird die erste Stufe, deren beste Spaltenzahl den Inhalt
    /// noch mindestens in Lesegröße (MinScale) ins Rechteck bringt. Passt keine, gewinnt die größte Darstellung.
    /// Die Spaltenzahlen hängen davon ab, wie viele Bereiche/Karten gerade eingeblendet sind.
    /// </summary>
    private List<(double MinScale, LayoutChoice[] Options)> BuildTiers()
    {
        int sections = Math.Max(1, _sections.Count(s => s.Keys.Any(Shown)));
        var fullCols = Enumerable.Range(1, Math.Min(4, sections)).ToArray();

        int[] MiniCols(bool narrow)
        {
            int n = Math.Max(1, _miniCards.Count(m => CardVisible(m, narrow)));
            return new[] { 1, 2, n }.Where(c => c <= n).Distinct().ToArray();
        }

        var tiers = new List<(double, LayoutChoice[])>
        {
            (0.85, fullCols.Select(c => new LayoutChoice(View.Full, 0, c, true)).ToArray()),
            (0.85, fullCols.Select(c => new LayoutChoice(View.Full, 1, c, true)).ToArray()),
        };
        bool header = Shown(Metrics.Header);
        foreach (int detail in new[] { 0, 1 })
        {
            foreach (bool h in header ? new[] { true, false } : new[] { false })
                tiers.Add((0.80, MiniCols(detail > 0).Select(c => new LayoutChoice(View.Mini, detail, c, h)).ToArray()));
        }
        return tiers;
    }

    private bool _layoutPending;

    private void ScheduleLayout()
    {
        if (_layoutPending) return;
        _layoutPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _layoutPending = false;
            ChooseLayout();
        }, DispatcherPriority.Render);
    }

    /// <summary>Wählt Ansicht, Detailstufe und Spaltenzahl passend zu Breite und Höhe des Rechtecks.</summary>
    private void ChooseLayout()
    {
        double aw = Root.ActualWidth - ContentBox.Margin.Left - ContentBox.Margin.Right;
        double ah = Root.ActualHeight - ContentBox.Margin.Top - ContentBox.Margin.Bottom;
        if (aw < 20 || ah < 20) return;

        LayoutChoice best = default;
        double bestScale = -1;
        foreach (var (minScale, options) in BuildTiers())
        {
            LayoutChoice tierBest = default;
            double tierScale = -1;
            foreach (var option in options)
            {
                var size = MeasureLayout(option);
                if (size.Width < 1 || size.Height < 1) continue;
                double scale = Math.Min(aw / size.Width, ah / size.Height);
                if (scale > tierScale)
                {
                    tierScale = scale;
                    tierBest = option;
                }
            }
            if (tierScale > bestScale)
            {
                bestScale = tierScale;
                best = tierBest;
            }
            if (tierScale >= minScale)
            {
                best = tierBest;
                break;
            }
            // Erreicht keine Stufe Lesegröße, bleibt "best" die insgesamt größte Darstellung.
        }
        if (bestScale > 0) ApplyLayout(best);
    }

    private Size MeasureLayout(LayoutChoice c)
    {
        ApplyLayout(c);
        // Ein echter Layout-Durchlauf statt panel.Measure(): Measure() liefert sonst eine zwischengespeicherte Größe,
        // wenn sich nur tiefer liegende Elemente (Spaltenzahl, Sichtbarkeit) geändert haben.
        // Die Viewbox misst ihren Inhalt ohne Begrenzung → DesiredSize ist die natürliche Größe der Ansicht.
        UpdateLayout();
        FrameworkElement panel = c.View == View.Full ? FullView : MiniView;
        return panel.DesiredSize;
    }

    private void ApplyLayout(LayoutChoice c)
    {
        bool full = c.View == View.Full;
        FullView.Visibility = Vis(full);
        MiniView.Visibility = Vis(!full);

        if (full)
        {
            SetSectionColumns(c.Columns);
            FullView.Width = c.Columns * SectionWidth + (c.Columns - 1) * SectionGap;

            bool details = c.Detail == 0;
            FpsSpark.Visibility = Vis(details && Shown(Metrics.Graphs));
            CpuSpark.Visibility = Vis(details && Shown(Metrics.Graphs));
            GpuSpark.Visibility = Vis(details && Shown(Metrics.Graphs));
            PowerSpark.Visibility = Vis(details && Shown(Metrics.Graphs));
            CpuCores.Visibility = Vis(details && Shown(Metrics.CpuCores));
        }
        else
        {
            bool narrow = c.Detail > 0;
            MiniGrid.Columns = c.Columns;
            MiniHeader.Visibility = Vis(c.Header && Shown(Metrics.Header));
            // In der schmalen Variante passt neben den Titel keine Uhr.
            if (MiniClockText.Parent is FrameworkElement clock) clock.Visibility = Vis(!narrow);

            foreach (var m in _miniCards)
            {
                m.Card.Visibility = Vis(CardVisible(m, narrow));
                m.Card.Width = narrow ? MiniCardNarrowWidth : MiniCardWidth;
                m.Sub.Visibility = Vis(!narrow && m.Subs.Any(s => Shown(s.Key)));
            }
        }
    }

    /// <summary>
    /// Verteilt die eingeblendeten Bereiche auf 1–4 Spalten. Ein Grid mit Auto-Zeilen statt UniformGrid, damit jede
    /// Zeile nur so hoch ist wie ihr höchster Bereich (sonst entstehen in der einspaltigen Ansicht Lücken).
    /// </summary>
    private void SetSectionColumns(int columns)
    {
        // Abstand zwischen den Spalten: Bereiche bekommen links/rechts je einen halben Abstand,
        // das Raster wird um denselben Betrag nach außen gezogen.
        SectionsGrid.Margin = new Thickness(-SectionGap / 2, 0, -SectionGap / 2, 0);
        var margin = new Thickness(SectionGap / 2, 12, SectionGap / 2, 0);

        var visible = new List<FrameworkElement>();
        foreach (var s in _sections)
        {
            bool on = s.Keys.Any(Shown);
            s.Section.Visibility = Vis(on);
            s.Section.Margin = margin;
            if (on) visible.Add(s.Section);
        }
        if (visible.Count == 0) visible.Add(_sections[0].Section); // nie ganz leer

        SectionsGrid.ColumnDefinitions.Clear();
        SectionsGrid.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++)
            SectionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < (visible.Count + columns - 1) / columns; i++)
            SectionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < visible.Count; i++)
        {
            Grid.SetColumn(visible[i], i % columns);
            Grid.SetRow(visible[i], i / columns);
        }
    }

    // ------------------------------------------------------------------ Bearbeiten-Modus

    /// <summary>
    /// Bearbeiten-Modus: Das Overlay nimmt Mausklicks an (aber nie den Tastaturfokus – das Spiel behält die Tastatur).
    /// </summary>
    public void BeginEditMode()
    {
        if (!IsOverlayVisible || IsEditMode) return;
        IsEditMode = true;
        ShowSizeText(_settings.OverlayWidth, _settings.OverlayHeight);
        EditLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        SetClickThrough(false);
        EditModeChanged?.Invoke();
    }

    public void EndEditMode()
    {
        if (!IsEditMode) return;
        if (EditLayer.IsMouseCaptured) EditLayer.ReleaseMouseCapture(); // → EndGesture()
        IsEditMode = false;
        EditLayer.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        SetClickThrough(true);
        EditModeChanged?.Invoke();
    }

    private void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = enabled ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    private enum Gesture { None, Move, Resize }

    private Gesture _gesture = Gesture.None;
    private Native.POINT _dragStart;
    private Native.RECT _dragRect;
    private int _gripSx, _gripSy; // +1 = rechte/untere Kante wird gezogen, -1 = linke/obere, 0 = bleibt

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private bool BeginGesture(Gesture gesture)
    {
        if (!IsEditMode || _hwnd == IntPtr.Zero || _gesture != Gesture.None) return false;
        if (!Native.GetCursorPos(out _dragStart) || !Native.GetWindowRect(_hwnd, out _dragRect)) return false;
        _gesture = gesture;
        if (!EditLayer.CaptureMouse())
        {
            _gesture = Gesture.None;
            return false;
        }
        return true;
    }

    private void OnEditLayerDown(object sender, MouseButtonEventArgs e)
    {
        if (BeginGesture(Gesture.Move)) e.Handled = true;
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle || handle.Tag is not string tag) return;
        var parts = tag.Split(',');
        _gripSx = int.Parse(parts[0], CultureInfo.InvariantCulture);
        _gripSy = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (BeginGesture(Gesture.Resize)) e.Handled = true; // nicht bis zur Fläche (= Verschieben) durchreichen
    }

    private void OnEditMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None || !Native.GetCursorPos(out var p)) return;
        // In Bildschirmpixeln rechnen: Das Fenster bewegt/verändert sich unter der Maus, Fensterkoordinaten wären instabil.
        int dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        var r = _dragRect;

        if (_gesture == Gesture.Move)
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }

        // Größe: nur die gezogenen Kanten bewegen sich, die gegenüberliegenden bleiben stehen.
        double dpi = Dpi;
        int minW = (int)Math.Ceiling(AppSettings.MinWidth * dpi);
        int minH = (int)Math.Ceiling(AppSettings.MinHeight * dpi);
        int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_gripSx > 0) right = Math.Max(left + minW, r.Right + dx);
        else if (_gripSx < 0) left = Math.Min(right - minW, r.Left + dx);
        if (_gripSy > 0) bottom = Math.Max(top + minH, r.Bottom + dy);
        else if (_gripSy < 0) top = Math.Min(bottom - minH, r.Top + dy);

        Native.SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        ShowSizeText((right - left) / dpi, (bottom - top) / dpi);
    }

    private void OnEditWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsEditMode || _gesture != Gesture.None) return;
        double f = e.Delta > 0 ? 1.05 : 1 / 1.05;
        _settings.OverlayWidth *= f;
        _settings.OverlayHeight *= f;
        ApplySize();
        LayoutCommitted?.Invoke();
        e.Handled = true;
    }

    private void OnWindowSizeChanged()
    {
        // Während des Ziehens bestimmt die Maus Lage und Größe, nicht die gespeicherte Ecke.
        if (IsOverlayVisible && _gesture == Gesture.None) Reposition();
    }

    private void EndGesture()
    {
        var g = _gesture;
        if (g == Gesture.None) return;
        _gesture = Gesture.None;

        if (g == Gesture.Resize && Native.GetWindowRect(_hwnd, out var r))
        {
            double dpi = Dpi;
            _settings.OverlayWidth = (r.Right - r.Left) / dpi;
            _settings.OverlayHeight = (r.Bottom - r.Top) / dpi;
        }
        CommitPlacement();
        ApplySize();   // WPF-Größe = gespeicherte Größe (in DIP)
        Reposition();  // auf den Bildschirm zurückholen, falls herausgezogen
        LayoutCommitted?.Invoke();
    }

    /// <summary>
    /// Merkt sich die aktuelle Lage als "nächste Bildschirmecke + Abstand". So bleibt das Overlay auch bei
    /// späteren Größenänderungen und anderen Monitoren an derselben Stelle verankert.
    /// </summary>
    private void CommitPlacement()
    {
        if (!Native.GetWindowRect(_hwnd, out var r) || !TryGetMonitor(false, out var b)) return;
        bool left = (r.Left + r.Right) / 2 < (b.Left + b.Right) / 2;
        bool top = (r.Top + r.Bottom) / 2 < (b.Top + b.Bottom) / 2;
        double dpi = Dpi;

        int ox = left ? r.Left - b.Left : b.Right - r.Right;
        int oy = top ? r.Top - b.Top : b.Bottom - r.Bottom;
        _settings.Position = (top ? "Top" : "Bottom") + (left ? "Left" : "Right");
        _settings.OffsetX = Math.Round(Math.Max(0, ox) / dpi);
        _settings.OffsetY = Math.Round(Math.Max(0, oy) / dpi);
    }

    // ------------------------------------------------------------------ Fenster-Setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Platziert das Overlay an der gespeicherten Ecke + Versatz.
    /// <paramref name="followForeground"/> = true (beim Einblenden): auf den Monitor der Vordergrund-Anwendung,
    /// sonst auf dem Monitor bleiben, auf dem das Overlay gerade ist.
    /// </summary>
    public void Reposition() => Reposition(followForeground: false);

    public void Reposition(bool followForeground)
    {
        if (_hwnd == IntPtr.Zero || _gesture != Gesture.None) return;
        if (!TryGetMonitor(followForeground, out var b)) return;
        if (!Native.GetWindowRect(_hwnd, out var wr)) return;

        int w = wr.Right - wr.Left;
        int h = wr.Bottom - wr.Top;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int ox = (int)Math.Round(Math.Max(0, _settings.OffsetX) * dpi);
        int oy = (int)Math.Round(Math.Max(0, _settings.OffsetY) * dpi);
        string pos = _settings.Position ?? "TopRight";

        int x = pos.EndsWith("Left", StringComparison.Ordinal) ? b.Left + ox : b.Right - w - ox;
        int y = pos.StartsWith("Bottom", StringComparison.Ordinal) ? b.Bottom - h - oy : b.Top + oy;

        // Immer ganz auf dem Bildschirm halten (z. B. nach dem Vergrößern oder auf einem kleineren Monitor).
        x = Math.Max(b.Left, Math.Min(x, b.Right - w));
        y = Math.Max(b.Top, Math.Min(y, b.Bottom - h));

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private bool TryGetMonitor(bool followForeground, out Native.RECT bounds)
    {
        bounds = default;
        IntPtr fg = followForeground ? Native.GetForegroundWindow() : IntPtr.Zero;
        IntPtr monitor = Native.MonitorFromWindow(fg != IntPtr.Zero ? fg : _hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(monitor, ref mi)) return false;
        bounds = mi.rcMonitor;
        return true;
    }

    private void UpdateFrameGeometry()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 50 || h < 50) return;
        const double cut = 20;

        var frame = new StreamGeometry();
        using (var ctx = frame.Open())
        {
            ctx.BeginFigure(new Point(cut, 0), true, true);
            ctx.PolyLineTo(new[]
            {
                new Point(w, 0), new Point(w, h - cut), new Point(w - cut, h),
                new Point(0, h), new Point(0, cut),
            }, true, true);
        }
        frame.Freeze();

        var accent = new StreamGeometry();
        using (var ctx = accent.Open())
        {
            // Ecke oben rechts
            ctx.BeginFigure(new Point(w - 58, 3.5), false, false);
            ctx.PolyLineTo(new[] { new Point(w - 3.5, 3.5), new Point(w - 3.5, 36) }, true, false);
            // Ecke unten links
            ctx.BeginFigure(new Point(3.5, h - 36), false, false);
            ctx.PolyLineTo(new[] { new Point(3.5, h - 3.5), new Point(58, h - 3.5) }, true, false);
            // Markierungen an den Schrägen
            ctx.BeginFigure(new Point(cut + 8, -1.5), false, false);
            ctx.LineTo(new Point(cut + 46, -1.5), true, false);
            ctx.BeginFigure(new Point(w - cut - 46, h + 1.5), false, false);
            ctx.LineTo(new Point(w - cut - 8, h + 1.5), true, false);
        }
        accent.Freeze();

        FramePath.Data = frame;
        FrameGlow.Data = frame;
        AccentPath.Data = accent;
        FxLayer.Clip = frame;
    }

    // ------------------------------------------------------------------ Ein-/Ausblenden

    public void ShowOverlay()
    {
        if (IsOverlayVisible) return;
        _state = State.Showing;

        ResetReadouts();
        Show();
        UpdateLayout();
        ChooseLayout(); // sofort, damit schon das erste Bild die passende Ansicht zeigt
        Reposition(followForeground: true);

        _hw.Start();
        _fpsError = _fps.Start();
        _tickCount = 0;
        OnTick();
        _tick.Start();
        StartAmbient();

        // "CRT power on": vertikal aufziehen + Flackern
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var open = new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
        open.Completed += (_, _) => { if (_state == State.Showing) _state = State.Visible; };
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, open);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });

        var flicker = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190))));
        flicker.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
        Root.BeginAnimation(OpacityProperty, flicker);
    }

    public void HideOverlay(Action? onHidden = null)
    {
        if (!IsOverlayVisible) return;
        EndEditMode();
        _state = State.Hiding;

        // Sofort alle Datenquellen stoppen – ab hier wird nichts mehr ausgelesen.
        _tick.Stop();
        _hw.Stop();
        _fps.Stop();

        var d = TimeSpan.FromMilliseconds(170);
        var close = new DoubleAnimation(0.02, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        close.Completed += (_, _) =>
        {
            if (_state != State.Hiding) return;
            StopAmbient();
            Hide();
            _state = State.Hidden;
            onHidden?.Invoke();
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, close);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, d));
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
    }

    /// <summary>Sofortiges Ausblenden ohne Animation (beim Beenden).</summary>
    public void HideImmediately()
    {
        EndEditMode();
        _tick.Stop();
        _hw.Stop();
        _fps.Stop();
        StopAmbient();
        Hide();
        _state = State.Hidden;
    }

    // ------------------------------------------------------------------ Ambient-Animationen

    private void StartAmbient()
    {
        GlitchA.BeginAnimation(TranslateTransform.XProperty, Glitch(-3, 2, -1.5));
        GlitchB.BeginAnimation(TranslateTransform.XProperty, Glitch(3, -2.5, 1.5));

        // Diskrete Keyframes → nur 2 Neuzeichnungen pro Zyklus statt Dauer-Rendering
        var blink = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(1200),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750))));
        LiveDot.BeginAnimation(OpacityProperty, blink);
        MiniLiveDot.BeginAnimation(OpacityProperty, blink);

        // Scan-Balken: 1,6 s Durchlauf, danach Pause
        double h = Math.Max(300, Root.ActualHeight);
        var scan = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(5200),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        scan.KeyFrames.Add(new DiscreteDoubleKeyFrame(-90, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scan.KeyFrames.Add(new LinearDoubleKeyFrame(h + 10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        scan.KeyFrames.Add(new DiscreteDoubleKeyFrame(-90, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1650))));
        ScanBarShift.BeginAnimation(TranslateTransform.YProperty, scan);
    }

    private static DoubleAnimationUsingKeyFrames Glitch(double a, double b, double c)
    {
        var g = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(4300),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        g.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        g.KeyFrames.Add(new DiscreteDoubleKeyFrame(a, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3700))));
        g.KeyFrames.Add(new DiscreteDoubleKeyFrame(b, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3760))));
        g.KeyFrames.Add(new DiscreteDoubleKeyFrame(c, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3830))));
        g.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3900))));
        g.Freeze();
        return g;
    }

    private void StopAmbient()
    {
        // Wichtig: laufende Animationen stoppen, sonst tickt WPFs Render-Loop auch im versteckten Zustand.
        GlitchA.BeginAnimation(TranslateTransform.XProperty, null);
        GlitchB.BeginAnimation(TranslateTransform.XProperty, null);
        LiveDot.BeginAnimation(OpacityProperty, null);
        MiniLiveDot.BeginAnimation(OpacityProperty, null);
        ScanBarShift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    // ------------------------------------------------------------------ Daten

    private void ResetReadouts()
    {
        _sensorsOnline = false;
        _driverMissing = false;
        StatusText.Text = "> SYNC SENSOR BUS ...";
        StatusText.Foreground = DimBrush;

        foreach (var tb in new[] { FrametimeText, LowText, ApiText, CpuTempText, CpuClockText, CpuPeakText, GpuTempText, GpuClockText, GpuMemText })
        {
            tb.Text = "--";
        }
        CpuTempText.Foreground = NormalBrush;
        GpuTempText.Foreground = NormalBrush;
        FpsText.Text = "---";
        CpuLoadText.Text = "--";
        GpuLoadText.Text = "--";

        MiniFpsText.Text = "---";
        foreach (var tb in new[] { MiniFrametimeText, MiniLowText, MiniCpuLoadText, MiniCpuTempText, MiniCpuClockText,
                                   MiniGpuLoadText, MiniGpuTempText, MiniGpuClockText })
        {
            tb.Text = "--";
        }
        MiniCpuTempText.Foreground = NormalBrush;
        MiniGpuTempText.Foreground = NormalBrush;

        TotalPowerText.Text = "---";
        CpuPowerText.Text = "--";
        GpuPowerText.Text = "--";
        MiniTotalPowerText.Text = "--";
        MiniCpuPowerText.Text = "CPU --";
        MiniGpuPowerText.Text = "GPU --";
        PowerSpark.Clear();

        CpuGauge.Snap(ArcGauge.ValueProperty, 0);
        GpuGauge.Snap(ArcGauge.ValueProperty, 0);
        CpuTempBar.Snap(SegmentBar.ValueProperty, 0);
        GpuTempBar.Snap(SegmentBar.ValueProperty, 0);
        FpsSpark.Clear();
        CpuSpark.Clear();
        GpuSpark.Clear();
        CpuCores.SetValues(Array.Empty<float>());
    }

    private void ApplySnapshot(HardwareSnapshot s)
    {
        // Ein Snapshot aus einem früheren Einblenden kann noch in der Dispatcher-Warteschlange stecken.
        if (!IsOverlayVisible || s.Generation != _hw.Generation) return;

        if (s.Error != null)
        {
            StatusText.Text = "> SENSOR FAULT :: " + s.Error;
            StatusText.Foreground = HotBrush;
            return;
        }

        if (!_sensorsOnline)
        {
            _sensorsOnline = true;
            UpdateStatusLine();
        }

        CpuSection.Tag = ShortName(s.CpuName);
        GpuSection.Tag = ShortName(s.GpuName);

        SetLoad(CpuGauge, CpuLoadText, s.CpuLoad);
        SetTemp(CpuTempText, CpuTempBar, s.CpuTemp);
        CpuClockText.Text = Mhz(s.CpuClock);
        CpuPeakText.Text = Mhz(s.CpuClockMax);
        CpuSpark.Push(s.CpuLoad ?? 0);
        CpuCores.SetValues(s.CoreLoads);

        SetLoad(GpuGauge, GpuLoadText, s.GpuLoad);
        SetTemp(GpuTempText, GpuTempBar, s.GpuTemp);
        GpuClockText.Text = Mhz(s.GpuCoreClock);
        GpuMemText.Text = Mhz(s.GpuMemClock);
        GpuSpark.Push(s.GpuLoad ?? 0);

        // Mini-Ansicht
        MiniCpuLoadText.Text = Percent(s.CpuLoad);
        SetMiniTemp(MiniCpuTempText, s.CpuTemp);
        MiniCpuClockText.Text = Ghz(s.CpuClock);
        MiniGpuLoadText.Text = Percent(s.GpuLoad);
        SetMiniTemp(MiniGpuTempText, s.GpuTemp);
        MiniGpuClockText.Text = Ghz(s.GpuCoreClock);

        // Ohne PawnIO-Treiber liefert LibreHardwareMonitor keine CPU-Temperatur/-Leistung → klar sagen statt "--".
        if (s.SensorDriverMissing)
        {
            CpuTempText.Text = "PAWNIO FEHLT";
            CpuTempText.Foreground = WarmBrush;
            MiniCpuTempText.Text = "PawnIO fehlt";
            MiniCpuTempText.Foreground = WarmBrush;
            if (!_driverMissing)
            {
                _driverMissing = true;
                UpdateStatusLine();
                SensorDriverMissing?.Invoke();
            }
        }

        // Stromverbrauch (CPU-Paket + Grafikkarte; nicht der Verbrauch an der Steckdose)
        float? total = s.TotalPower;
        TotalPowerText.Text = total is float t ? t.ToString("0", CultureInfo.InvariantCulture) : "--";
        CpuPowerText.Text = Watt(s.CpuPower);
        GpuPowerText.Text = Watt(s.GpuPower);
        PowerSpark.Push(total ?? 0);
        MiniTotalPowerText.Text = Watt(total);
        MiniCpuPowerText.Text = "CPU " + Watt(s.CpuPower);
        MiniGpuPowerText.Text = "GPU " + Watt(s.GpuPower);
    }

    private static string Watt(float? w) =>
        w is float f ? f.ToString("0", CultureInfo.InvariantCulture) + " W" : "--";

    private static string Percent(float? v) =>
        v is float f ? f.ToString("0", CultureInfo.InvariantCulture) + "%" : "--";

    private static string Ghz(float? mhz) =>
        mhz is float f ? (f / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " GHz" : "--";

    private static void SetMiniTemp(TextBlock text, float? value)
    {
        text.Text = value is float t ? t.ToString("0", CultureInfo.InvariantCulture) + " °C" : "--";
        text.Foreground = value is float v ? (v >= 85 ? HotBrush : v >= 70 ? WarmBrush : NormalBrush) : NormalBrush;
    }

    private void UpdateStatusLine()
    {
        if (_driverMissing)
        {
            StatusText.Text = "> CPU-SENSOR :: TREIBER PAWNIO FEHLT (pawnio.eu)" + (_fpsError != null ? " // FPS OFFLINE" : "");
            StatusText.Foreground = WarmBrush;
        }
        else if (_fpsError != null)
        {
            StatusText.Text = "> SENSORS ONLINE // FPS OFFLINE :: " + _fpsError;
            StatusText.Foreground = WarmBrush;
        }
        else
        {
            StatusText.Text = _sensorsOnline ? "> LINK ESTABLISHED // ALL SENSORS ONLINE" : "> SYNC SENSOR BUS ...";
            StatusText.Foreground = DimBrush;
        }
    }

    private static void SetLoad(ArcGauge gauge, TextBlock text, float? value)
    {
        if (value is float v)
        {
            text.Text = v.ToString("0", CultureInfo.InvariantCulture);
            gauge.AnimateTo(ArcGauge.ValueProperty, v);
        }
        else
        {
            text.Text = "--";
            gauge.AnimateTo(ArcGauge.ValueProperty, 0);
        }
    }

    private static void SetTemp(TextBlock text, SegmentBar bar, float? value)
    {
        if (value is float t)
        {
            text.Text = t.ToString("0", CultureInfo.InvariantCulture) + " °C";
            text.Foreground = t >= 85 ? HotBrush : t >= 70 ? WarmBrush : NormalBrush;
            bar.AnimateTo(SegmentBar.ValueProperty, t);
        }
        else
        {
            text.Text = "--";
            text.Foreground = NormalBrush;
            bar.AnimateTo(SegmentBar.ValueProperty, 0);
        }
    }

    private static string Mhz(float? v) =>
        v is float f ? f.ToString("0", CultureInfo.InvariantCulture) + " MHz" : "--";

    private static string ShortName(string name)
    {
        string n = name
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" CPU", "", StringComparison.Ordinal)
            .Replace(" Processor", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();
        return n.ToUpperInvariant();
    }

    private void OnTick()
    {
        if (!IsOverlayVisible) return;
        _tickCount++;

        ClockText.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        MiniClockText.Text = ClockText.Text;

        // Vordergrund-Anwendung = Ziel für die FPS-Messung (das Overlay selbst nimmt nie den Fokus)
        int target = ResolveTargetPid(Native.GetForegroundWindow());
        if (target != 0)
        {
            _fps.SetTarget(target);
            TargetText.Text = "> TARGET :: " + ProcessName(target);
        }

        if (_fpsError != null)
        {
            FpsText.Text = "N/A";
            MiniFpsText.Text = "N/A";
            if (_tickCount == 1) UpdateStatusLine();
        }
        else
        {
            var st = _fps.GetStats();
            FpsText.Text = st.Fps > 0 ? Math.Round(st.Fps).ToString(CultureInfo.InvariantCulture) : "0";
            FrametimeText.Text = st.Fps > 0 ? st.FrameTimeMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "--";
            LowText.Text = st.Low1 > 0 ? Math.Round(st.Low1).ToString(CultureInfo.InvariantCulture) : "--";
            ApiText.Text = st.Api;
            FpsSpark.Push(st.Fps);

            MiniFpsText.Text = FpsText.Text;
            MiniFrametimeText.Text = FrametimeText.Text;
            MiniLowText.Text = st.Low1 > 0 ? "1% " + LowText.Text : "1% --";
        }

        HexText.Text = $"0x{_rng.Next(0x100000, 0xFFFFFF):X6} // LINK.OK";

        // Manche Spiele setzen sich selbst "topmost" – periodisch wieder nach vorne holen (ohne Fokus).
        if (_tickCount % 4 == 0 && _hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// PID des Prozesses, der im Vordergrundfenster zeichnet. Bei UWP-/Store-Spielen gehört das Fenster
    /// ApplicationFrameHost.exe, präsentiert wird aber im Kindfenster eines anderen Prozesses.
    /// </summary>
    private int ResolveTargetPid(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return 0;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == _ownPid) return 0;

        if (string.Equals(ProcessName((int)pid), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint host = pid, child = 0;
            Native.EnumChildWindows(fg, (h, _) =>
            {
                Native.GetWindowThreadProcessId(h, out uint p);
                if (p != 0 && p != host)
                {
                    child = p;
                    return false; // gefunden, Aufzählung beenden
                }
                return true;
            }, IntPtr.Zero);
            if (child != 0 && child != _ownPid) pid = child;
        }
        return (int)pid;
    }

    private string ProcessName(int pid)
    {
        // PIDs werden von Windows wiederverwendet → Namen nicht ewig cachen.
        long now = Environment.TickCount64;
        if (_procNames.TryGetValue(pid, out var cached) && now - cached.Time < 10_000) return cached.Name;
        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            name = "PID " + pid;
        }
        if (_procNames.Count > 64) _procNames.Clear();
        _procNames[pid] = (name, now);
        return name;
    }
}
