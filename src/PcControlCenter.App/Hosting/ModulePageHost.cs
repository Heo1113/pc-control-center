using System.Windows;
using System.Windows.Controls;
using PcControlCenter.Core;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Hosting;

/// <summary>
/// Shows the selected tab. Each module's pages live in their own container whose resources are that module's
/// ModuleResources.xaml (its original theme, converters and view templates; see <see cref="ModuleResourceScope"/> for
/// what the views see while they load), so implicit templates and styles of different modules never clash.
/// Containers stay alive while hidden, so a module page keeps its state (scroll position, selection) between visits.
/// </summary>
public sealed class ModulePageHost : Grid
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(nameof(Tab), typeof(ShellTab), typeof(ModulePageHost),
        new PropertyMetadata(null, (d, e) => ((ModulePageHost)d).OnTabChanged((ShellTab?)e.OldValue, (ShellTab?)e.NewValue)));

    private readonly Dictionary<string, ContentControl> _containers = [];

    public ShellTab? Tab
    {
        get => (ShellTab?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    private void OnTabChanged(ShellTab? _, ShellTab? newTab)
    {
        if (newTab is null)
        {
            foreach (var c in _containers.Values)
            {
                c.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var key = newTab.ModuleId ?? "shell";
        if (!_containers.TryGetValue(key, out var container))
        {
            container = new ContentControl { Focusable = false, IsTabStop = false };
            if (newTab.ModuleId is { } id && ModuleResourceScope.For(id) is { } resources)
            {
                // Implicit view templates and styles, dynamic resources. Load-time lookups: see ModuleResourceScope.
                container.Resources.MergedDictionaries.Add(resources);
            }

            container.ContentTemplate = (DataTemplate)Application.Current.FindResource("ShellTabTemplate");
            _containers[key] = container;
            Children.Add(container);
        }

        container.Content = newTab;
        foreach (var (k, c) in _containers)
        {
            c.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
