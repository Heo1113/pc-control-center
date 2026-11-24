using WindowsAiOperator.Core.Automation;

namespace WindowsAiOperator.Presentation;

/// <summary>Runs work on the UI thread. WPF uses its dispatcher; tests run inline. (Same shape as the user's project B.)</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Message boxes and pickers. Every destructive action goes through <see cref="Confirm"/> first.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);

    void Info(string title, string message);

    void Error(string title, string message);

    string? PickFolder(string title);

    string? OpenFile(string title, string filter);

    string? SaveFile(string title, string filter, string defaultFileName);
}

/// <summary>Shows approval requests and plan reviews in a window that stays on top of the target app.</summary>
public interface IApprovalPresenter
{
    void ShowApproval(ViewModels.ApprovalViewModel approval);

    void ShowPlanReview(ViewModels.PlanReviewViewModel review);

    void Close(object viewModel);
}

/// <summary>Draws a temporary rectangle around a UI element on screen (inspector).</summary>
public interface IHighlighter
{
    void Highlight(PixelRect rectangle);
}

/// <summary>Operations that need the application itself.</summary>
public interface IAppHost
{
    void ApplyFontScale(double scale);

    void ApplyTheme(string theme);

    void OpenFolder(string path);

    /// <summary>Restarts the application (after a database restore). Running tasks are stopped first.</summary>
    void Restart();
}

/// <summary>No-op implementations for tests and the command line.</summary>
public sealed class NullUiServices : IDialogService, IApprovalPresenter, IHighlighter, IAppHost
{
    public bool ConfirmAnswer { get; set; }
    public List<string> Messages { get; } = [];

    public bool Confirm(string title, string message)
    {
        Messages.Add($"confirm: {title}");
        return ConfirmAnswer;
    }

    public void Info(string title, string message) => Messages.Add($"info: {title}: {message}");

    public void Error(string title, string message) => Messages.Add($"error: {title}: {message}");

    public string? PickFolder(string title) => null;

    public string? OpenFile(string title, string filter) => null;

    public string? SaveFile(string title, string filter, string defaultFileName) => null;

    public void ShowApproval(ViewModels.ApprovalViewModel approval)
    {
    }

    public void ShowPlanReview(ViewModels.PlanReviewViewModel review)
    {
    }

    public void Close(object viewModel)
    {
    }

    public void Highlight(PixelRect rectangle)
    {
    }

    public void ApplyFontScale(double scale)
    {
    }

    public void ApplyTheme(string theme)
    {
    }

    public void OpenFolder(string path)
    {
    }

    public void Restart()
    {
    }
}

/// <summary>A choice in a drop-down list.</summary>
public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public enum Tone
{
    Neutral,
    Good,
    Warning,
    Bad,
    Info,
}
