using System.Windows;
using System.Windows.Media;

namespace ControllerStudioPro.Controls;

/// <summary>Fluent theme brushes for code that draws by hand.</summary>
static class Theme
{
    public static Brush Get(FrameworkElement el, string key, Brush fallback) =>
        el.TryFindResource(key) as Brush ?? fallback;

    public static Brush Accent(FrameworkElement el) => Get(el, "AccentFillColorDefaultBrush", Brushes.DodgerBlue);
    public static Brush Text(FrameworkElement el) => Get(el, "TextFillColorPrimaryBrush", Brushes.Black);
    public static Brush TextSecondary(FrameworkElement el) => Get(el, "TextFillColorSecondaryBrush", Brushes.Gray);
    public static Brush Fill(FrameworkElement el) => Get(el, "ControlFillColorSecondaryBrush", Brushes.LightGray);
    public static Brush FillStrong(FrameworkElement el) => Get(el, "ControlStrongFillColorDefaultBrush", Brushes.Gray);
    public static Brush Stroke(FrameworkElement el) => Get(el, "ControlStrokeColorDefaultBrush", Brushes.LightGray);
    public static Brush Divider(FrameworkElement el) => Get(el, "DividerStrokeColorDefaultBrush", Brushes.LightGray);

    /// <summary>A frozen copy at the given opacity (theme brushes are resource-bound and can't be frozen as-is).</summary>
    public static Brush WithOpacity(Brush b, double opacity)
    {
        if (b is SolidColorBrush s)
        {
            var c = new SolidColorBrush(s.Color) { Opacity = s.Opacity * opacity };
            c.Freeze();
            return c;
        }
        var clone = b.CloneCurrentValue();
        clone.Opacity = opacity;
        return clone;
    }

    public static FormattedText Label(FrameworkElement el, string text, double size, Brush brush, bool bold = false) =>
        new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
                         bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(el).PixelsPerDip);
}
