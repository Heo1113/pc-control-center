using System.Windows;
using PcControlCenter.Core;

namespace PcControlCenter.App.Hosting;

/// <summary>
/// Makes each module's resources (its ModuleResources.xaml: theme, converters, view templates) visible to its unchanged
/// views both while they load and while they run.
/// </summary>
/// <remarks>
/// A view resolves <c>{StaticResource}</c> while its XAML loads, which happens in its constructor, before WPF puts it into
/// its module's container. At that moment only the view's own resources and <see cref="Application.Resources"/> are
/// searched, not the container. So the shell gives each module's dictionary (one instance) to that module's container
/// for everything resolved later (implicit view templates and styles, dynamic resources), and copies to the application
/// the keyed resources that exactly one module defines and the application does not (B's <c>OkBrush</c>, <c>Field</c>,
/// <c>StatusLine</c>, E's <c>Banner</c> ...). A key that several modules define stays in the containers only; the shell's
/// own XAML uses explicit keys for those, and <c>scripts/xaml-resource-check.py</c> fails CI if a module view would look
/// such a key up while loading. Keys the application already has (A's palette, the shared converters) are not replaced.
/// </remarks>
public static class ModuleResourceScope
{
    private static readonly Dictionary<string, Uri> Sources = new()
    {
        [ModuleIds.LocalAi] = new Uri("pack://application:,,,/LocalAiPerf.App;component/ModuleResources.xaml"),
        [ModuleIds.GpuEconomics] = new Uri("pack://application:,,,/GpuProfit.App;component/ModuleResources.xaml"),
        [ModuleIds.Diagnostics] = new Uri("pack://application:,,,/WinDiag.App;component/ModuleResources.xaml"),
        [ModuleIds.Operator] = new Uri("pack://application:,,,/WindowsAiOperator.App;component/ModuleResources.xaml"),
    };

    private static readonly Dictionary<string, ResourceDictionary?> Loaded = [];

    /// <summary>Copied to the application: key and the module that defines it.</summary>
    public static IReadOnlyDictionary<string, string> Promoted { get; private set; } = new Dictionary<string, string>();

    /// <summary>Keys several modules define (kept in the containers only).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Shared { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The module's dictionary, loaded once; null if it could not be loaded (logged by <see cref="Install"/>).</summary>
    public static ResourceDictionary? For(string moduleId)
    {
        if (Loaded.TryGetValue(moduleId, out var dictionary))
        {
            return dictionary;
        }

        dictionary = Sources.TryGetValue(moduleId, out var uri) ? new ResourceDictionary { Source = uri } : null;
        Loaded[moduleId] = dictionary;
        return dictionary;
    }

    /// <summary>Loads the four dictionaries and copies the single-owner keys to <paramref name="application"/>. Call before any window or view is created.</summary>
    public static void Install(ResourceDictionary application, Action<string, Exception?> log)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var moduleId in Sources.Keys)
        {
            ResourceDictionary? dictionary;
            try
            {
                dictionary = For(moduleId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // This module's pages will show their own load failure; the other modules are unaffected.
                Loaded[moduleId] = null;
                log($"{moduleId}: 모듈 리소스를 불러오지 못했습니다", ex);
                continue;
            }

            if (dictionary is null)
            {
                continue;
            }

            foreach (var key in KeysOf(dictionary).Distinct())
            {
                if (!application.Contains(key))
                {
                    (owners.TryGetValue(key, out var list) ? list : owners[key] = []).Add(moduleId);
                }
            }
        }

        var promoted = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, modules) in owners)
        {
            if (modules.Count == 1)
            {
                application[key] = Loaded[modules[0]]![key];
                promoted[key] = modules[0];
            }
            else
            {
                shared[key] = modules;
            }
        }

        Promoted = promoted;
        Shared = shared;
        log($"모듈 리소스: 앱 수준 {promoted.Count}개 ({string.Join(", ", promoted.Keys.Order(StringComparer.Ordinal))}), " +
            $"여러 모듈 공통이라 각 모듈 안에서만 {shared.Count}개 ({string.Join(", ", shared.Keys.Order(StringComparer.Ordinal))})", null);
    }

    /// <summary>String keys of the dictionary and its merged dictionaries (implicit styles and view templates have type keys and stay in the container).</summary>
    private static IEnumerable<string> KeysOf(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var key in KeysOf(merged))
            {
                yield return key;
            }
        }

        foreach (var key in dictionary.Keys)
        {
            if (key is string text)
            {
                yield return text;
            }
        }
    }
}
