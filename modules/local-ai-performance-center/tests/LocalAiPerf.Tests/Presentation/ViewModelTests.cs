using System.Collections.Concurrent;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.MockStrata;
using LocalAiPerf.Presentation.Services;
using LocalAiPerf.Presentation.ViewModels;
using LocalAiPerf.Recording;
using Xunit;

namespace LocalAiPerf.Tests.Presentation;

internal sealed class QueueDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    public void Pump()
    {
        while (_queue.TryDequeue(out var action))
        {
            action();
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public string? PromptResult { get; set; } = "VM 테스트 세션";
    public bool ConfirmResult { get; set; } = true;
    public string? SavePath { get; set; }
    public string? FolderPath { get; set; }
    public List<string> Messages { get; } = [];

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null) => SavePath;
    public string? OpenFile(string title, string filter, string? initialDirectory = null) => SavePath;
    public string? PickFolder(string title, string? initialDirectory = null) => FolderPath;
    public bool Confirm(string title, string message) => ConfirmResult;
    public void Info(string title, string message) => Messages.Add("info: " + message);
    public void Error(string title, string message) => Messages.Add("error: " + message);
    public string? Prompt(string title, string message, string defaultValue) => PromptResult;
}

internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];

    public void OpenFolder(string path) => Opened.Add(path);
}

public class ViewModelTests
{
    private static async Task<bool> Until(QueueDispatcher ui, Func<bool> condition, int timeoutMs = 8000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            ui.Pump();
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        ui.Pump();
        return condition();
    }

    [Fact]
    public async Task Main_view_model_drives_all_screens_with_mock_sensors_and_mock_strata()
    {
        using var dir = new TempDir();
        await using var strata = new MockStrataServer();
        strata.Start();
        var paths = new AppPaths(dir.Path, false);
        var settings = new AppSettings();
        settings.Sampling.IntervalMs = 500;
        settings.Strata.BaseUrl = strata.BaseUrl;
        settings.Recording.AutoRecordOnStartup = false;
        settings.Normalize();
        var store = new SettingsStore(paths.SettingsPath);
        store.Save(settings);
        var ui = new QueueDispatcher();
        var dialogs = new FakeDialogs { FolderPath = Path.Combine(dir.Path, "exports") };
        var shell = new FakeShell();

        await using var service = new MonitoringService(paths, settings, new MonitoringOptions { UseMockSensors = true });
        await service.StartAsync();
        using var main = new MainViewModel(service, store, ui, dialogs, shell);

        // Dashboard
        Assert.True(await Until(ui, () => main.FramesReceived >= 2 && main.Dashboard.GpuUtil != "N/A" && main.Dashboard.AnswerTokS != "N/A"));
        Assert.True(main.IsMock);
        Assert.Contains("%", main.Dashboard.GpuUtil);
        Assert.Equal("연결됨", main.Dashboard.RuntimeState);
        Assert.Equal(2, main.Dashboard.UtilizationChart!.Series.Count);
        Assert.Equal(Freshness.Fresh, main.Freshness);

        // Hardware and runtime pages
        main.NavigateCommand.Execute(main.Hardware);
        Assert.True(await Until(ui, () => main.Hardware.Sensors.Count > 0 && main.Hardware.GpuUtilChart is not null));
        Assert.Contains(main.Hardware.Sensors, s => s.RequiresElevation);
        main.NavigateCommand.Execute(main.Runtime);
        Assert.True(await Until(ui, () => main.Runtime.Model.Count > 0 && main.Runtime.StateText == "연결됨"));

        // Two manual sessions
        await main.StartRecordingCommand.ExecuteAsync(null);
        Assert.True(main.IsRecording);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await main.StopRecordingCommand.ExecuteAsync(null);
        dialogs.PromptResult = "VM 테스트 세션 2";
        await main.StartRecordingCommand.ExecuteAsync(null);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await main.StopRecordingCommand.ExecuteAsync(null);
        Assert.False(main.IsRecording);

        // History
        main.NavigateCommand.Execute(main.History);
        // No UI SynchronizationContext in tests: the load continues on a pool thread, so wait for its last step (the chart).
        Assert.True(await Until(ui, () => main.History.Sessions.Count == 2 && main.History.Stats.Count > 0 && main.History.Chart is not null && !main.History.IsBusy));
        Assert.Contains(main.History.Sessions, s => s.Name == "VM 테스트 세션");
        Assert.NotNull(main.History.Chart);
        await main.History.ExportCsvCommand.ExecuteAsync(null);
        Assert.Single(shell.Opened);
        Assert.True(File.Exists(Path.Combine(shell.Opened[0], "samples.csv")));

        // Compare
        main.NavigateCommand.Execute(main.Compare);
        Assert.True(await Until(ui, () => main.Compare.Sessions.Count == 2));
        foreach (var s in main.Compare.Sessions)
        {
            s.IsSelected = true;
        }

        Assert.Equal(2, main.Compare.Selected.Count);
        Assert.NotEqual(main.Compare.Sessions[0].ColorHex, main.Compare.Sessions[1].ColorHex);
        await main.Compare.CompareCommand.ExecuteAsync(null);
        Assert.True(main.Compare.HasResult, main.Compare.StatusText);
        Assert.NotEmpty(main.Compare.Rows);
        Assert.Contains(main.Compare.Warnings, w => w.Kind == "mock");
        Assert.NotNull(main.Compare.Chart);
        var firstColor = main.Compare.Sessions[0].ColorHex;
        main.Compare.Sessions[1].IsSelected = false;
        Assert.Equal(firstColor, main.Compare.Sessions[0].ColorHex); // survivors keep their color

        // Settings: invalid URL is rejected, a valid change is applied and saved
        main.NavigateCommand.Execute(main.Settings);
        await Until(ui, () => main.Settings.DatabaseInfo.Count > 0);
        main.Settings.StrataUrl = "http://10.1.2.3:8080";
        await main.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains(main.Settings.Messages, m => m.Contains("https"));
        main.Settings.StrataUrl = strata.BaseUrl;
        main.Settings.IntervalMs = 1000;
        await main.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1000, service.Settings.Sampling.IntervalMs);
        Assert.Equal(1000, new SettingsStore(paths.SettingsPath).Load().Settings.Sampling.IntervalMs);
        Assert.True(await Until(ui, () => main.Freshness == Freshness.Fresh));
    }
}
