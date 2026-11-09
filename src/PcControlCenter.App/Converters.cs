using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PcControlCenter.Presentation;

namespace PcControlCenter.App;

/// <summary>Status colors of the shell (same brushes as A's palette; always shown next to a text label).</summary>
public sealed class LevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Level.Good => "GoodBrush",
            Level.Warning => "WarningBrush",
            Level.Error => "CriticalBrush",
            WinDiag.Presentation.ViewModels.StatusLevel level => level switch
            {
                WinDiag.Presentation.ViewModels.StatusLevel.Good => "GoodBrush",
                WinDiag.Presentation.ViewModels.StatusLevel.Warning => "WarningBrush",
                WinDiag.Presentation.ViewModels.StatusLevel.Critical => "CriticalBrush",
                _ => "TextMutedBrush",
            },
            WinDiag.Core.Events.DiagSeverity severity => severity >= WinDiag.Core.Events.DiagSeverity.Error ? "CriticalBrush"
                : severity == WinDiag.Core.Events.DiagSeverity.Warning ? "WarningBrush" : "TextMutedBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => Visibility.Collapsed,
        string s => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible,
        ICollection c => c.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
