using CommunityToolkit.Mvvm.ComponentModel;
using LocalAiPerf.Core;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>Base of the six screens. Only the visible page receives live frames.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public abstract string Glyph { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Called on the UI thread for every collected frame while the page is visible.</summary>
    public virtual void OnFrame(TelemetryFrame frame)
    {
    }

    /// <summary>Called on the UI thread when the page becomes visible.</summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;
}
