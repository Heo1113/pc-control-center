using System.ComponentModel;
using System.Windows;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App;

/// <summary>Topmost approval / plan-review window. Closing it with the title-bar button counts as "no".</summary>
public partial class ApprovalWindow : Window
{
    private readonly object _viewModel;
    private bool _closingFromCode;
    private bool _closed;

    public ApprovalWindow(object viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Host.Content = viewModel;
        DataContext = viewModel;
        if (viewModel is ApprovalViewModel approval)
        {
            approval.PropertyChanged += OnAnswered;
        }
        else if (viewModel is PlanReviewViewModel review)
        {
            review.PropertyChanged += OnAnswered;
        }
    }

    public void CloseFromCode()
    {
        if (_closed)
        {
            return;
        }

        _closingFromCode = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingFromCode)
        {
            switch (_viewModel)
            {
                case ApprovalViewModel { IsAnswered: false } approval:
                    approval.DenyCommand.Execute(null);
                    break;
                case PlanReviewViewModel { IsAnswered: false } review:
                    review.CancelCommand.Execute(null);
                    break;
            }
        }

        base.OnClosing(e);
    }

    private void OnAnswered(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApprovalViewModel.IsAnswered) or nameof(PlanReviewViewModel.IsAnswered))
        {
            Dispatcher.InvokeAsync(CloseFromCode);
        }
    }
}
