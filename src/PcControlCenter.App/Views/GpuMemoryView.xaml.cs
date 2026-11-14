using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Views;

/// <summary>
/// Hardware › GPU 메모리. View state is kept by the view model (remembered in the shell settings): sort, hidden columns and
/// the detail panel's height. While the pointer is over the table its rows keep their places (the values still update), so
/// a row never moves away under the mouse. Ctrl+F finds, Esc clears, Enter or ↓ goes to the table; the right mouse button
/// copies (on a row) or chooses columns (on a header); Ctrl+C copies the selected row.
/// </summary>
public partial class GpuMemoryView : UserControl
{
    private static readonly HashSet<string> Numeric = ["Dedicated", "Delta", "Shared", "Gpu", "Committed"];
    private readonly ContextMenu _rowMenu = new();
    private readonly ContextMenu _columnMenu = new();
    private readonly MenuItem _kindItem;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private GpuMemoryViewModel? _vm;
    private bool _hovering;

    public GpuMemoryView()
    {
        InitializeComponent();
        _rowMenu.Items.Add(Item("행 복사", "Ctrl+C", CopyRow));
        _rowMenu.Items.Add(Item("이름 복사", null, () => Copy(_vm?.SelectedProcess?.Name, "이름")));
        _rowMenu.Items.Add(Item("PID 복사", null, () => Copy(_vm?.SelectedProcess?.Pid.ToString(CultureInfo.InvariantCulture), "PID")));
        _rowMenu.Items.Add(Item("상세 복사", null, () => Copy(_vm?.DetailText(), "상세")));
        _rowMenu.Items.Add(new Separator());
        _kindItem = Item("이 종류만 보기", null, () =>
        {
            if (_vm?.SelectedProcess is { } row)
            {
                _vm.FilterKindCommand.Execute(row.Kind);
            }
        });
        _rowMenu.Items.Add(_kindItem);
        Table.ContextMenu = _rowMenu;
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice.Visibility = Visibility.Collapsed;
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Table.Sorting += OnSorting;
        Table.MouseEnter += (_, _) => SetHover(true);
        Table.MouseLeave += (_, _) => SetHover(false);
        Table.PreviewMouseRightButtonDown += OnRightButtonDown;
        Table.ContextMenuOpening += OnContextMenuOpening;
        Table.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyRow(), (_, e) => e.CanExecute = _vm?.SelectedProcess is not null));
        PreviewKeyDown += OnPreviewKeyDown;
        FilterBox.PreviewKeyDown += OnFilterKeyDown;
        Splitter.DragCompleted += (_, _) => _vm?.SetDetailHeight(DetailRow.ActualHeight);
        Splitter.MouseDoubleClick += (_, _) => ResetDetailHeight();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GpuMemoryViewModel vm || ReferenceEquals(vm, _vm))
        {
            return;
        }

        _vm = vm;
        vm.PropertyChanged += OnViewModelChanged;
        if (Table.Items is ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            // With no explicit properties the view re-sorts on the properties of its sort descriptions.
            live.IsLiveSorting = true;
        }

        ApplySort(vm.SortColumn, vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        UpdateColumns();
        if (vm.DetailHeight > 0)
        {
            DetailRow.Height = new GridLength(vm.DetailHeight);
            TableRow.Height = new GridLength(1, GridUnitType.Star);
        }

        ScrollToSelection();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // The view model outlives this control (the page is re-created when another shell page was shown).
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
        }

        _noticeTimer.Stop();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GpuMemoryViewModel.ShowGpuColumn) or nameof(GpuMemoryViewModel.HasBaseline))
        {
            UpdateColumns();
        }
        else if (e.PropertyName == nameof(GpuMemoryViewModel.SelectedProcess))
        {
            Dispatcher.BeginInvoke(ScrollToSelection, DispatcherPriority.Background);
        }
    }

    private void ScrollToSelection()
    {
        // Not while the pointer is over the table: the rows would move under it.
        if (_vm?.SelectedProcess is { } row && !_hovering)
        {
            Table.ScrollIntoView(row);
        }
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Numbers start largest-first like Task Manager, text A→Z; a second click reverses.
        var direction = e.Column.SortDirection switch
        {
            ListSortDirection.Ascending => ListSortDirection.Descending,
            ListSortDirection.Descending => ListSortDirection.Ascending,
            _ => Numeric.Contains(path) ? ListSortDirection.Descending : ListSortDirection.Ascending,
        };
        ApplySort(path, direction);
        _vm?.SetSort(path, direction == ListSortDirection.Descending);
    }

    private void ApplySort(string path, ListSortDirection direction)
    {
        var items = Table.Items;
        using (items.DeferRefresh())
        {
            items.SortDescriptions.Clear();
            items.SortDescriptions.Add(new SortDescription(path, direction));
            // Equal values keep a fixed order, so rows do not swap places on every reading.
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

    /// <summary>Rows keep their places while the pointer is over the table; leaving it re-sorts once.</summary>
    private void SetHover(bool hovering)
    {
        if (_hovering == hovering || Table.Items is not ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            return;
        }

        _hovering = hovering;
        live.IsLiveSorting = !hovering;
        if (!hovering && _vm is not null)
        {
            ApplySort(_vm.SortColumn, _vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        }
    }

    private void UpdateColumns()
    {
        if (_vm is null)
        {
            return;
        }

        foreach (var column in Table.Columns)
        {
            var key = column.SortMemberPath;
            var visible = !_vm.HiddenColumns.Contains(key)
                          && (key != nameof(GpuProcessRow.Adapter) || _vm.ShowGpuColumn)
                          && (key != nameof(GpuProcessRow.Delta) || _vm.HasBaseline);
            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The menu acts on the row under the pointer.
        if (_vm is not null && FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { Item: GpuProcessRow row })
        {
            _vm.SelectedProcess = row;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridColumnHeader>(source) is not null)
        {
            BuildColumnMenu();
            Table.ContextMenu = _columnMenu;
            return;
        }

        if (FindAncestor<DataGridRow>(source) is null || _vm?.SelectedProcess is not { } row)
        {
            e.Handled = true; // empty space: no menu
            return;
        }

        _kindItem.Header = _vm.KindFilter == row.Kind ? "모든 종류 보기" : $"{row.KindLabel}만 보기";
        Table.ContextMenu = _rowMenu;
    }

    private void BuildColumnMenu()
    {
        _columnMenu.Items.Clear();
        foreach (var column in Table.Columns)
        {
            var key = column.SortMemberPath;
            var item = new MenuItem
            {
                Header = column.Header is TextBlock text ? text.Text : column.Header?.ToString() ?? key,
                IsCheckable = true,
                IsChecked = _vm?.HiddenColumns.Contains(key) != true,
                IsEnabled = key != nameof(GpuProcessRow.Name),
                StaysOpenOnClick = true,
            };
            if (key == nameof(GpuProcessRow.Adapter))
            {
                item.ToolTip = "켜 두어도 어댑터가 둘 이상일 때만 보입니다";
            }
            else if (key == nameof(GpuProcessRow.Delta))
            {
                item.ToolTip = "켜 두어도 기준점을 잡았을 때만 보입니다";
            }

            item.Click += (_, _) =>
            {
                _vm?.SetColumnHidden(key, !item.IsChecked);
                UpdateColumns();
            };
            _columnMenu.Items.Add(item);
        }

        _columnMenu.Items.Add(new Separator());
        _columnMenu.Items.Add(Item("기본 열로 되돌리기", null, () =>
        {
            _vm?.ResetColumns();
            UpdateColumns();
        }));
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
        else if (e.Key is Key.Enter or Key.Down)
        {
            FocusTable();
            e.Handled = true;
        }
    }

    /// <summary>Moves the keyboard to the table, on the selected row (the first one when none is selected).</summary>
    private void FocusTable()
    {
        if (Table.Items.Count == 0)
        {
            return;
        }

        if (_vm is not null && _vm.SelectedProcess is null)
        {
            _vm.SelectedProcess = Table.Items[0] as GpuProcessRow;
        }

        var item = Table.SelectedItem ?? Table.Items[0];
        Table.ScrollIntoView(item);
        Table.UpdateLayout();
        var cell = Table.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row
            ? FindChild<DataGridCellsPresenter>(row)?.ItemContainerGenerator.ContainerFromIndex(0) as DataGridCell
            : null;
        if (cell is not null)
        {
            cell.Focus();
        }
        else
        {
            Table.Focus();
        }
    }

    private void ResetDetailHeight()
    {
        TableRow.Height = new GridLength(3, GridUnitType.Star);
        DetailRow.Height = new GridLength(2, GridUnitType.Star);
        _vm?.SetDetailHeight(0);
    }

    private void CopyRow()
    {
        if (_vm?.SelectedProcess is { } row)
        {
            Copy(GpuMemoryViewModel.RowText(row), "행");
        }
    }

    private void OnCopyDetail(object sender, RoutedEventArgs e) => Copy(_vm?.DetailText(), "상세");

    private void OnCopyBaseline(object sender, RoutedEventArgs e) => Copy(_vm?.BaselineReport(), "비교");

    private void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            ShowNotice($"{what} 복사함");
        }
        catch (ExternalException)
        {
            ShowNotice("클립보드를 다른 프로그램이 쓰고 있어 복사하지 못했습니다");
        }
    }

    private void ShowNotice(string text)
    {
        Notice.Text = text;
        Notice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private static MenuItem Item(string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? FindChild<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
