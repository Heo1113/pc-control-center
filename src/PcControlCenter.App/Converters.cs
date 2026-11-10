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

/// <summary>
/// Shared at application level: the shell's views and, while they load, the modules' unchanged views use this key
/// (a view resolves {StaticResource} before it is inside its module's container). It therefore accepts what A, B and C
/// bind (true, or a count above zero, e.g. A's <c>Notices.Count</c>) and what E binds (true).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true || value is int and > 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Shared at application level like <see cref="BoolToVisibilityConverter"/>. Hidden for null, an empty or blank text, a
/// count of zero (E binds <c>Parameters.Count</c> and <c>Hints.Count</c>) and an empty collection; visible for anything
/// else (A, B and C bind texts and objects). The one difference to A/B/C's own converter: a text of only spaces is hidden.
/// </summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => Visibility.Collapsed,
        string s => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible,
        int count => count > 0 ? Visibility.Visible : Visibility.Collapsed,
        ICollection c => c.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Width of one card so that cards fill their row: as many columns of at least <see cref="MinWidth"/> as fit share the
/// available width (each card's horizontal margin included). The dashboard's cards use it, so no empty strip is left at
/// the right whatever the window width (1024×768 with the compact menu, the default size, a 4K screen).
/// </summary>
public sealed class CardWidthConverter : IValueConverter
{
    public double MinWidth { get; set; } = 340;

    /// <summary>Left plus right margin of one card.</summary>
    public double HorizontalMargin { get; set; } = 12;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || available <= 0)
        {
            return MinWidth;
        }

        var columns = Math.Max(1, (int)Math.Floor(available / (MinWidth + HorizontalMargin)));

        // One pixel of slack so rounding never pushes the last card of a row onto the next line.
        return Math.Max(0, Math.Floor(available / columns) - HorizontalMargin - 1);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

