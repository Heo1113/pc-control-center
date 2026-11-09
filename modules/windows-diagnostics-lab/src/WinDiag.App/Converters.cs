using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinDiag.Core.Events;
using WinDiag.Presentation.ViewModels;

namespace WinDiag.App;

/// <summary>Maps states to the reserved status brushes. A text label is always shown next to the color. (Adapted from project A.)</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusLevel.Good => "GoodBrush",
            StatusLevel.Warning => "WarningBrush",
            StatusLevel.Critical => "CriticalBrush",
            DiagSeverity.Critical => "CriticalBrush",
            DiagSeverity.Error => "SeriousBrush",
            DiagSeverity.Warning => "WarningBrush",
            DiagSeverity.Info => "TextSecondaryBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

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
