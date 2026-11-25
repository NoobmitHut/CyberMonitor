using System;
using System.Windows;
using System.Windows.Media;

namespace CyberMonitor.Controls;

/// <summary>Segmentierter 270°-Ring mit Skalenstrichen (0–100 %).</summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(ArcGauge),
        new FrameworkPropertyMetadata(Colors.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double Thickness { get; set; } = 10;

    private const double StartAngle = 135;
    private const double SweepAngle = 270;
    private static readonly DashStyle Segments = Freeze(new DashStyle(new[] { 0.34, 0.16 }, 0));

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 30) return;

        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2 - 8;
        var a = Accent;

        // Skalenstriche außen
        var tickPen = Freeze(new Pen(Brush(a, 0x80), 1));
        var tickPenMajor = Freeze(new Pen(Brush(a, 0xC0), 1.4));
        for (int i = 0; i <= 30; i++)
        {
            double ang = StartAngle + SweepAngle * i / 30;
            bool major = i % 5 == 0;
            double r1 = r + Thickness / 2 + 2.5;
            double r2 = r1 + (major ? 5 : 2.5);
            dc.DrawLine(major ? tickPenMajor : tickPen, P(c, r1, ang), P(c, r2, ang));
        }

        // Track
        var track = Freeze(new Pen(Brush(a, 0x26), Thickness) { DashStyle = Segments });
        dc.DrawGeometry(null, track, Arc(c, r, StartAngle, SweepAngle));

        // Wert
        double v = Math.Clamp(Value, 0, 100);
        if (v > 0.3)
        {
            double sweep = SweepAngle * v / 100;
            var valuePen = Freeze(new Pen(Brush(a, 0xFF), Thickness) { DashStyle = Segments });
            dc.DrawGeometry(null, valuePen, Arc(c, r, StartAngle, sweep));

            var tip = P(c, r, StartAngle + sweep);
            dc.DrawEllipse(Brushes.White, null, tip, 2.4, 2.4);
        }

        // Innenring
        var inner = Freeze(new Pen(Brush(a, 0x40), 1) { DashStyle = Freeze(new DashStyle(new[] { 2.0, 3.0 }, 0)) });
        double ri = r - Thickness / 2 - 5;
        dc.DrawEllipse(null, inner, c, ri, ri);
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(c, r, startDeg), false, false);
            ctx.ArcTo(P(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180,
                SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static Point P(Point c, double r, double deg)
    {
        double rad = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    internal static SolidColorBrush Brush(Color c, byte alpha) =>
        Freeze(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));

    internal static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
