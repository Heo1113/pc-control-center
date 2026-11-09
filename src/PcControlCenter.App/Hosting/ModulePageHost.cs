using System.Windows;
using System.Windows.Controls;
using PcControlCenter.Core;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Hosting;

/// <summary>
/// Shows the selected tab. Each module's pages live in their own container whose resources are that module's
/// ModuleResources.xaml (its original theme, converters and view templates), so the modules' unchanged views find
/// everything they expected from their old App.xaml and MainWindow.xaml, and keys of different modules never clash.
/// Containers stay alive while hidden, so a module page keeps its state (scroll position, selection) between visits.
/// </summary>
public sealed class ModulePageHost : Grid
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(nameof(Tab), typeof(ShellTab), typeof(ModulePageHost),
        new PropertyMetadata(null, (d, e) => ((ModulePageHost)d).OnTabChanged((ShellTab?)e.OldValue, (ShellTab?)e.NewValue)));

    private static readonly Dictionary<string, Uri> ModuleResources = new()
    {
        [ModuleIds.LocalAi] = new Uri("pack://application:,,,/LocalAiPerf.App;component/ModuleResources.xaml"),
        [ModuleIds.GpuEconomics] = new Uri("pack://application:,,,/GpuProfit.App;component/ModuleResources.xaml"),
        [ModuleIds.Diagnostics] = new Uri("pack://application:,,,/WinDiag.App;component/ModuleResources.xaml"),
        [ModuleIds.Operator] = new Uri("pack://application:,,,/WindowsAiOperator.App;component/ModuleResources.xaml"),
    };

    private readonly Dictionary<string, ContentControl> _containers = [];

    public ShellTab? Tab
    {
        get => (ShellTab?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    /// <summary>Additional module resource dictionaries (the Operator view library registers its own).</summary>
    public static void Register(string moduleId, Uri resources) => ModuleResources[moduleId] = resources;

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
            if (newTab.ModuleId is { } id && ModuleResources.TryGetValue(id, out var uri))
            {
                // Must be in place before any module view is created: their StaticResource lookups happen at load.
                container.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
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
