using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Views;

/// <summary>
/// Hardware › GPU 메모리. Sorting is view state and lives here: the table keeps its order while values change (largest
/// dedicated memory first until a header is clicked), numbers sort largest-first on the first click like Task Manager, and
/// equal values keep a fixed order so rows do not swap on every reading. Ctrl+F goes to the filter, Esc clears it.
/// </summary>
public partial class GpuMemoryView : UserControl
{
    private static readonly HashSet<string> Numeric =
        [nameof(GpuProcessRow.Dedicated), nameof(GpuProcessRow.Shared), nameof(GpuProcessRow.Gpu), nameof(GpuProcessRow.Committed)];

    public GpuMemoryView()
    {
        InitializeComponent();
        Loaded += (_, _) => ConfigureSorting();
        Table.Sorting += OnSorting;
        PreviewKeyDown += OnPreviewKeyDown;
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
    }

    private void ConfigureSorting()
    {
        var items = Table.Items;
        if (items is ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            // With no explicit properties the view re-sorts on the properties of its sort descriptions.
            live.IsLiveSorting = true;
        }

        // The rows' collection view outlives this control (tabs re-create their pages): keep a sort chosen earlier.
        var primary = items.SortDescriptions.Count > 0 ? items.SortDescriptions[0] : new SortDescription(nameof(GpuProcessRow.Dedicated), ListSortDirection.Descending);
        Apply(primary.PropertyName, primary.Direction);
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var direction = e.Column.SortDirection switch
        {
            ListSortDirection.Ascending => ListSortDirection.Descending,
            ListSortDirection.Descending => ListSortDirection.Ascending,
            _ => Numeric.Contains(path) ? ListSortDirection.Descending : ListSortDirection.Ascending,
        };
        Apply(path, direction);
    }

    private void Apply(string path, ListSortDirection direction)
    {
        var items = Table.Items;
        using (items.DeferRefresh())
        {
            items.SortDescriptions.Clear();
            items.SortDescriptions.Add(new SortDescription(path, direction));
            foreach (var tieBreaker in new[] { nameof(GpuProcessRow.Pid), nameof(GpuProcessRow.Adapter) })
            {
                if (tieBreaker != path)
                {
                    items.SortDescriptions.Add(new SortDescription(tieBreaker, ListSortDirection.Ascending));
                }
            }
        }

        foreach (var column in Table.Columns)
        {
            column.SortDirection = column.SortMemberPath == path ? direction : null;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FilterBox.Text.Length > 0)
        {
            FilterBox.Clear();
            e.Handled = true;
        }
    }
}
