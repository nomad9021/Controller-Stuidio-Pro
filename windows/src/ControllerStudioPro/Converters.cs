using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ControllerStudioPro;

/// <summary>Shows a slider value the way the Linux version does: pct, zone, ms, hz or curve.</summary>
public sealed class FormatConverter : IValueConverter
{
    public static string Format(double v, string? kind)
    {
        int i = (int)Math.Round(v);
        return kind switch
        {
            "pct" => $"{i}%",
            "zone" => $"{i * 10}%",
            "ms" => $"{i} ms",
            "hz" => $"{i} Hz",
            "curve" => i == 0 ? "Linear" : i > 0 ? $"Gentle {i}" : $"Quick {-i}",
            _ => i.ToString(CultureInfo.CurrentCulture),
        };
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Format(System.Convert.ToDouble(value, CultureInfo.InvariantCulture), parameter as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value's text is one of the parameter's |-separated values.</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        bool match = ((parameter as string) ?? "").Split('|').Contains(text, StringComparer.OrdinalIgnoreCase);
        return match != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HexToBrush : IValueConverter
{
    public static SolidColorBrush Brush(string? hex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex ?? "#888888"));
            b.Freeze();
            return b;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Brush(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
