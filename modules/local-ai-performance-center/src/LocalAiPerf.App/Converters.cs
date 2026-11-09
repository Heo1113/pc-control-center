using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Presentation.ViewModels;

namespace LocalAiPerf.App;

/// <summary>Maps states to the reserved status brushes. A text label is always shown next to the color.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusLevel.Good => "GoodBrush",
            StatusLevel.Warning => "WarningBrush",
            StatusLevel.Critical => "CriticalBrush",
            Freshness.Fresh => "GoodBrush",
            Freshness.Stale => "WarningBrush",
            Freshness.Stopped => "CriticalBrush",
            RuntimeConnectionState.Connected => "GoodBrush",
            RuntimeConnectionState.Limited or RuntimeConnectionState.Timeout or RuntimeConnectionState.AuthRequired => "WarningBrush",
            RuntimeConnectionState.Incompatible or RuntimeConnectionState.ServerError or RuntimeConnectionState.ConfigurationError => "CriticalBrush",
            true => "CriticalBrush", // recording dot
            _ => "TextMutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Compare direction (1 better, -1 worse) to text brushes; neutral stays secondary ink.</summary>
public sealed class DirectionBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value switch { 1 => "GoodBrush", -1 => "SeriousBrush", _ => "TextSecondaryBrush" }) as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DirectionTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { 1 => "▲ 개선", -1 => "▼ 저하", _ => "" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true || value is int and > 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is a non-empty string or a non-null object.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && ColorConverter.ConvertFromString(hex) is Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Event severity (0 info, 1 warning, 2 error) to a text brush.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value switch { 2 => "CriticalBrush", 1 => "WarningBrush", _ => "TextSecondaryBrush" }) as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
