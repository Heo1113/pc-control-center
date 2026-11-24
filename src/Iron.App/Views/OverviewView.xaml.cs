using System.Windows;
using System.Windows.Controls;
using Iron.App.Presentation;

namespace Iron.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        OverviewList.SizeChanged += (_, _) => FitRows();
        DataContextChanged += (_, _) => FitRows();
    }

    /// <summary>The overview's process table shows exactly as many rows as its panel can hold (no half row, no scroll bar).</summary>
    private void FitRows()
    {
        if (DataContext is not ShellViewModel vm || OverviewList.ActualHeight <= 0 || TryFindResource("Iron.RowHeight") is not double row)
        {
            return;
        }

        vm.Processes.SetOverviewLimit((int)Math.Floor(OverviewList.ActualHeight / (row + 1)));
    }
}
