using System.Windows;

namespace LocalAiPerf.App;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string message, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Input.Text = defaultValue;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            Input.Focus();
            return;
        }

        DialogResult = true;
    }
}
