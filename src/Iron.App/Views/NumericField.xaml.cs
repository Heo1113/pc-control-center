using System.Windows;
using System.Windows.Controls;

namespace Iron.App.Views;

public partial class NumericField : UserControl
{
    public static readonly DependencyProperty ShowRangeProperty =
        DependencyProperty.Register(nameof(ShowRange), typeof(bool), typeof(NumericField), new PropertyMetadata(true));

    public NumericField() => InitializeComponent();

    /// <summary>Show "허용 55~95 °C" next to the input.</summary>
    public bool ShowRange
    {
        get => (bool)GetValue(ShowRangeProperty);
        set => SetValue(ShowRangeProperty, value);
    }
}
