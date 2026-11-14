using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PcControlCenter.App.Controls;

/// <summary>Carries the page's DataContext to places outside the visual tree (DataGrid columns).</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>"#RRGGBB" from a view model to a (cached, frozen) brush.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string hex && hex.Length > 0 ? SegmentBar.BrushOf(hex) : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
