using System.Windows;

namespace WinDiag.App;

/// <summary>Short text prompt. Copied from project A (LocalAiPerf.App/PromptWindow, commit 5b6aae1) with the namespace changed.</summary>
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
