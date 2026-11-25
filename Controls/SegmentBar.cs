using System;
using System.Windows;
using System.Windows.Media;

namespace CyberMonitor.Controls;

/// <summary>LED-Balken mit Farbverlauf Cyan → Gelb → Rot (für Temperaturen).</summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum { get; set; } = 100;
    public int Segments { get; set; } = 22;

    private static readonly Color Cool = Color.FromRgb(0x00, 0xF0, 0xFF);
    private static readonly Color Warm = Color.FromRgb(0xFC, 0xEE, 0x0A);
    private static readonly Color Hot = Color.FromRgb(0xFF, 0x2A, 0x4D);

    private Brush[]? _on, _off;

    private void EnsureBrushes()
    {
        if (_on != null && _on.Length == Segments) return;
        _on = new Brush[Segments];
        _off = new Brush[Segments];
        for (int i = 0; i < Segments; i++)
        {
            double t = Segments == 1 ? 0 : (double)i / (Segments - 1);
            var c = t < 0.6 ? Lerp(Cool, Warm, t / 0.6) : Lerp(Warm, Hot, (t - 0.6) / 0.4);
            _on[i] = ArcGauge.Brush(c, 0xFF);
            _off[i] = ArcGauge.Brush(c, 0x1E);
        }
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 2) return;
        EnsureBrushes();

        const double gap = 2;
        int n = Segments;
        double segW = (w - gap * (n - 1)) / n;
        double lit = Math.Clamp(Value / Maximum, 0, 1) * n;

        for (int i = 0; i < n; i++)
        {
            double x = i * (segW + gap);
            var rect = new Rect(x, 0, segW, h);
            dc.DrawRectangle(_off![i], null, rect);
            if (i < Math.Floor(lit))
            {
                dc.DrawRectangle(_on![i], null, rect);
            }
            else if (i < lit)
            {
                dc.PushOpacity(lit - i);
                dc.DrawRectangle(_on![i], null, rect);
                dc.Pop();
            }
        }
    }
}
