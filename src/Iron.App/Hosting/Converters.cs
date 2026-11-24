using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Iron.App.Presentation;

namespace Iron.App.Hosting;

internal static class Res
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>Status LED color: online olive, alert red, disconnected grey, loading amber.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusKind.Online => Res.Brush("IronStatusOnline"),
        StatusKind.Alert => Res.Brush("IronStatusError"),
        StatusKind.Loading => Res.Brush("IronAccentAmber"),
        _ => Res.Brush("IronLedOff"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Status text color on the graphite title bar.</summary>
public sealed class StatusInkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusKind.Alert => Res.Brush("IronInkErrorOnBezel"),
        StatusKind.Loading => Res.Brush("IronAccentAmber"),
        StatusKind.Disconnected => Res.Brush("IronInkBezelMuted"),
        _ => Res.Brush("IronInkBezel"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PinBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PinLevel.Over => Res.Brush("IronStatusError"),
        PinLevel.Approaching => Res.Brush("IronAccentAmber"),
        PinLevel.Normal => Res.Brush("IronAccentOrange"),
        _ => Res.Brush("IronSegmentOff"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class FluxBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FluxState.Sending => Res.Brush("IronStatusOnline"),
        FluxState.Conflict => Res.Brush("IronStatusWarn"),
        FluxState.Checking => Res.Brush("IronAccentAmber"),
        _ => Res.Brush("IronLedOff"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>IsChecked of a radio-style switch: value equals the parameter (enum or string); checking sets the parameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        return targetType.IsEnum ? Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true) : parameter;
    }
}

/// <summary>true → Visible; parameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            on = !on;
        }

        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Picks one of two brush resource keys ("TrueKey|FalseKey") by a bool.</summary>
public sealed class BoolBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var keys = (parameter as string ?? "").Split('|');
        return Res.Brush(value is true ? keys[0] : keys.Length > 1 ? keys[1] : keys[0]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
