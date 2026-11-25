using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CyberMonitor.Controls;

/// <summary>Verlaufsdiagramm (Linie + Neon-Flächenverlauf) mit Ringpuffer.</summary>
public sealed class Sparkline : FrameworkElement
{
    private const int Capacity = 60;

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.Cyan, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((Sparkline)d)._brushesDirty = true));

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>Fester Maximalwert (z. B. 100 für %), ignoriert bei AutoScale.</summary>
    public double Maximum { get; set; } = 100;

    public bool AutoScale { get; set; }

    /// <summary>Einheit für die Max-Beschriftung bei AutoScale.</summary>
    public string Unit { get; set; } = "";

    private readonly double[] _buf = new double[Capacity];
    private int _head, _count;

    private bool _brushesDirty = true;
    private Pen? _line, _grid;
    private Brush? _fill, _dot, _label;

    public void Push(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        _buf[_head] = value;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        InvalidateVisual();
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        InvalidateVisual();
    }

    private double At(int i) => _buf[(_head - _count + i + Capacity) % Capacity]; // 0 = ältester

    private void EnsureBrushes()
    {
        if (!_brushesDirty) return;
        var a = Accent;
        _line = ArcGauge.Freeze(new Pen(ArcGauge.Brush(a, 0xFF), 1.6) { LineJoin = PenLineJoin.Round });
        _grid = ArcGauge.Freeze(new Pen(ArcGauge.Brush(a, 0x1C), 1));
        _dot = ArcGauge.Brush(a, 0xFF);
        _label = ArcGauge.Brush(a, 0x99);
        _fill = ArcGauge.Freeze(new LinearGradientBrush(
            Color.FromArgb(0x66, a.R, a.G, a.B), Color.FromArgb(0x00, a.R, a.G, a.B), 90));
        _brushesDirty = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 6) return;
        EnsureBrushes();

        // Raster
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(_grid, new Point(0, y), new Point(w, y));
        }
        for (int i = 0; i <= 6; i++)
        {
            double x = Math.Round(w * i / 6) + 0.5;
            dc.DrawLine(_grid, new Point(x, h - 3), new Point(x, h));
        }

        if (_count < 2) return;

        double max = Maximum;
        if (AutoScale)
        {
            double m = 0;
            for (int i = 0; i < _count; i++) m = Math.Max(m, At(i));
            max = Math.Max(10, Math.Ceiling(m * 1.15 / 10) * 10);
        }

        double step = w / (Capacity - 1);
        Point Pt(int i)
        {
            double x = w - (_count - 1 - i) * step;
            double y = h - Math.Clamp(At(i) / max, 0, 1) * (h - 2) - 1;
            return new Point(x, y);
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var first = Pt(0);
            lc.BeginFigure(first, false, false);
            ac.BeginFigure(new Point(first.X, h), true, true);
            ac.LineTo(first, false, false);
            for (int i = 1; i < _count; i++)
            {
                var p = Pt(i);
                lc.LineTo(p, true, true);
                ac.LineTo(p, false, false);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _line, line);
        dc.DrawEllipse(_dot, null, Pt(_count - 1), 2.6, 2.6);

        if (AutoScale)
        {
            var ft = new FormattedText(max.ToString("0", CultureInfo.InvariantCulture) + Unit,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 9,
                _label, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(2, 0));
        }
    }
}
