using System.Windows;
using System.Windows.Controls;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is InspectorViewModel viewModel)
        {
            viewModel.SelectedElement = e.NewValue as ElementNodeViewModel;
        }
    }
}
