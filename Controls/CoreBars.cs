using System;
using System.Windows;
using System.Windows.Media;

namespace CyberMonitor.Controls;

/// <summary>"Core-Matrix": ein vertikaler LED-Balken pro CPU-Thread.</summary>
public sealed class CoreBars : FrameworkElement
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(CoreBars),
        new FrameworkPropertyMetadata(Colors.Cyan, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((CoreBars)d)._on = null));

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Color PeakColor { get; set; } = Color.FromRgb(0xFF, 0x2A, 0x6D);

    private const int Rows = 7;
    private float[] _values = Array.Empty<float>();
    private Brush? _on, _off, _peak;

    public void SetValues(float[] values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int n = _values.Length;
        if (w < 10 || h < 10 || n == 0) return;

        if (_on == null)
        {
            _on = ArcGauge.Brush(Accent, 0xE6);
            _off = ArcGauge.Brush(Accent, 0x18);
            _peak = ArcGauge.Brush(PeakColor, 0xFF);
        }

        double gapX = n > 24 ? 1.5 : 2.5;
        const double gapY = 1.5;
        double bw = (w - gapX * (n - 1)) / n;
        double bh = (h - gapY * (Rows - 1)) / Rows;

        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(_values[i], 0, 100);
            int lit = v <= 0.5 ? 0 : (int)Math.Ceiling(v / 100 * Rows);
            double x = i * (bw + gapX);
            for (int r = 0; r < Rows; r++)
            {
                double y = h - (r + 1) * bh - r * gapY;
                Brush b = r < lit ? (r >= Rows - 1 ? _peak! : _on) : _off!;
                dc.DrawRectangle(b, null, new Rect(x, y, bw, bh));
            }
        }
    }
}
