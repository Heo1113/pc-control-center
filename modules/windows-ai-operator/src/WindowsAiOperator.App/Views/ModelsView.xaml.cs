using System.Windows;
using System.Windows.Controls;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App.Views;

public partial class ModelsView : UserControl
{
    public ModelsView() => InitializeComponent();

    /// <summary>The key goes straight from the password box to the encrypted store and the box is cleared.</summary>
    private void OnStoreKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModelsViewModel viewModel)
        {
            viewModel.StoreApiKey(KeyBox.Password);
            KeyBox.Clear();
        }
    }
}
