using System.Windows;

namespace WindowsAiOperator.TestTarget;

public partial class TestDialog : Window
{
    public TestDialog() => InitializeComponent();

    public bool OptionChecked => DialogOptionCheckBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
