using System.Windows.Controls;

namespace Iron.App.Views;

public partial class TrayView : UserControl
{
    public TrayView()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        OpenButton.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenRequested;
}
