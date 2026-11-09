using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LocalAiPerf.Presentation.ViewModels;

namespace LocalAiPerf.App.Views;

/// <summary>The comparison table has one column pair per selected session, so its columns are built here.</summary>
public partial class CompareView : UserControl
{
    private CompareViewModel? _vm;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Headers.CollectionChanged -= OnHeadersChanged;
        }

        _vm = DataContext as CompareViewModel;
        if (_vm is not null)
        {
            _vm.Headers.CollectionChanged += OnHeadersChanged;
            BuildColumns();
        }
    }

    private void OnHeadersChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildColumns();

    private void BuildColumns()
    {
        RowsGrid.Columns.Clear();
        RowsGrid.Columns.Add(new DataGridTextColumn { Header = "지표", Binding = new Binding(nameof(CompareRow.Metric)), Width = 170 });
        RowsGrid.Columns.Add(new DataGridTextColumn { Header = "단위", Binding = new Binding(nameof(CompareRow.Unit)), Width = 55 });
        RowsGrid.Columns.Add(new DataGridTextColumn { Header = "출처", Binding = new Binding(nameof(CompareRow.Origin)), Width = 70 });
        if (_vm is null)
        {
            return;
        }

        for (var i = 0; i < _vm.Headers.Count; i++)
        {
            RowsGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _vm.Headers[i],
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 150,
                CellTemplate = CellTemplate(i),
            });
        }

        RowsGrid.Columns.Add(new DataGridTextColumn { Header = "비고", Binding = new Binding(nameof(CompareRow.Note)), Width = 150 });
    }

    private static DataTemplate CellTemplate(int index)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2));
        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetBinding(TextBlock.TextProperty, new Binding($"Cells[{index}].Value"));
        value.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(value);
        var delta = new FrameworkElementFactory(typeof(TextBlock));
        delta.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var text = new MultiBinding { StringFormat = "{0} {1}" };
        text.Bindings.Add(new Binding($"Cells[{index}].Delta"));
        text.Bindings.Add(new Binding($"Cells[{index}].Direction") { Converter = new DirectionTextConverter() });
        delta.SetBinding(TextBlock.TextProperty, text);
        delta.SetBinding(TextBlock.ForegroundProperty, new Binding($"Cells[{index}].Direction") { Converter = new DirectionBrushConverter() });
        panel.AppendChild(delta);
        return new DataTemplate { VisualTree = panel };
    }
}
