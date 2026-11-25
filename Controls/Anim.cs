using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace CyberMonitor.Controls;

internal static class Anim
{
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Weicher Übergang vom aktuellen (ggf. animierten) Wert zum Zielwert.</summary>
    public static void AnimateTo(this UIElement el, DependencyProperty dp, double to, int ms = 650)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        a.Freeze();
        el.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Animation entfernen und Wert hart setzen.</summary>
    public static void Snap(this UIElement el, DependencyProperty dp, double value)
    {
        el.BeginAnimation(dp, null);
        el.SetValue(dp, value);
    }

    private static T Freeze<T>(T f) where T : System.Windows.Freezable
    {
        f.Freeze();
        return f;
    }
}
