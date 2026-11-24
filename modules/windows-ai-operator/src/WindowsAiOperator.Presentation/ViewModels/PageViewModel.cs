using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsAiOperator.Presentation.ViewModels;

public abstract partial class PageViewModel(string title, string icon, string key) : ObservableObject
{
    /// <summary>Korean page title shown in the navigation.</summary>
    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Icon { get; } = icon;

    /// <summary>Stable English key (smoke tests, screenshots).</summary>
    public string Key { get; } = key;

    [ObservableProperty]
    public partial string? Notice { get; set; }

    public virtual void OnNavigatedTo()
    {
    }

    public virtual void OnNavigatedFrom()
    {
    }
}
