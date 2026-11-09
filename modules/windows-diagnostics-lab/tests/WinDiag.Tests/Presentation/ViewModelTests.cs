using WinDiag.Collection;
using WinDiag.Collection.Sources;
using WinDiag.Core;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Presentation;
using WinDiag.Presentation.ViewModels;

namespace WinDiag.Tests.Presentation;

internal sealed class FakeDialogs(string folder) : IDialogService
{
    public bool ConfirmAnswer { get; set; }
    public string? PromptAnswer { get; set; } = "테스트 기준선";
    public string? OpenAnswer { get; set; }
    public List<string> Log { get; } = [];

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        Log.Add("save:" + title);
        return Path.Combine(folder, defaultFileName);
    }

    public string? OpenFile(string title, string filter, string? initialDirectory = null)
    {
        Log.Add("open:" + title);
        return OpenAnswer;
    }

    public string? PickFolder(string title, string? initialDirectory = null) => folder;

    public bool Confirm(string title, string message)
    {
        Log.Add("confirm:" + title);
        return ConfirmAnswer;
    }

    public void Info(string title, string message) => Log.Add("info:" + title);

    public void Error(string title, string message) => Log.Add("error:" + title);

    public string? Prompt(string title, string message, string defaultValue) => PromptAnswer;
}

internal sealed class FakeHost : IAppHost
{
    public List<string> Restores { get; } = [];

    public Task RestoreAndRestartAsync(string backupPath)
    {
        Restores.Add(backupPath);
        return Task.CompletedTask;
    }
}

internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];

    public void OpenFolder(string path) => Opened.Add(path);

    public void OpenFile(string path) => Opened.Add(path);
}

/// <summary>The real view models on a real engine (mock sensors, virtual scenario data), without WPF.</summary>
internal sealed class AppHarness : IAsyncDisposable
{
    private readonly TempDir _dir;

    private AppHarness(TempDir dir, DiagnosticsEngine engine, FakeDialogs dialogs, FakeHost host, MainViewModel main, SettingsStore settings)
    {
        _dir = dir;
        Engine = engine;
        Dialogs = dialogs;
        Host = host;
        Main = main;
        SettingsStore = settings;
    }

    public DiagnosticsEngine Engine { get; }
    public FakeDialogs Dialogs { get; }
    public FakeHost Host { get; }
    public MainViewModel Main { get; }
    public SettingsStore SettingsStore { get; }
    public string Folder => _dir.Path;

    public static async Task<AppHarness> CreateAsync(bool demo = true)
    {
        var dir = new TempDir();
        var paths = AppPaths.Resolve(Path.Combine(dir.Path, "data"), dir.Path);
        var settings = new AppSettings();
        settings.Normalize();
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = paths, Settings = settings, UseMockSensors = true, EventSource = new MemoryEventSource("empty", false), StartBackgroundLoops = false,
            ScanDumps = false, Masker = PrivacyMasker.Generic,
        });
        await engine.PollNowAsync(); // first scan of the (empty) real logs: records their status
        if (demo)
        {
            await engine.SeedVirtualScenarioAsync();
        }

        var dialogs = new FakeDialogs(dir.Path);
        var host = new FakeHost();
        var store = new SettingsStore(paths.SettingsPath);
        var context = new ViewContext
        {
            Engine = engine, SettingsStore = store, Ui = new InlineDispatcher(), Dialogs = dialogs, Shell = new FakeShell(), ExportMasker = PrivacyMasker.Generic,
            Host = host,
        };
        return new AppHarness(dir, engine, dialogs, host, new MainViewModel(context), store);
    }

    public async ValueTask DisposeAsync()
    {
        Main.Dispose();
        await Engine.StopAsync();
        _dir.Dispose();
    }
}

public class ViewModelTests
{
    [Fact]
    public async Task Main_view_model_has_seven_pages_and_status()
    {
        await using var h = await AppHarness.CreateAsync();
        Assert.Equal(["대시보드", "이벤트 탐색", "하드웨어", "타임라인", "진단", "비교", "설정"], h.Main.Pages.Select(p => p.Title));
        Assert.Same(h.Main.Dashboard, h.Main.CurrentPage);
        Assert.True(h.Main.Dashboard.IsActive);
        h.Main.Tick();
        Assert.True(h.Main.IsMock);
        Assert.True(h.Main.HasVirtualData);
        Assert.Contains("MOCK", h.Main.AppTitle);
        Assert.False(string.IsNullOrEmpty(h.Main.DatabaseText));
    }

    [Fact]
    public async Task Dashboard_summarises_and_acknowledge_resets_new_events()
    {
        await using var h = await AppHarness.CreateAsync();
        var dashboard = h.Main.Dashboard;
        await dashboard.RefreshAsync();
        Assert.Equal(6, dashboard.Warnings.Count);
        Assert.NotNull(dashboard.Trend);
        Assert.Equal(2, dashboard.RecentCrashes.Count);
        Assert.Equal(StatusLevel.Critical, dashboard.OverallLevel);
        Assert.NotEmpty(dashboard.Channels);

        await dashboard.AcknowledgeCommand.ExecuteAsync(null);
        Assert.Empty(dashboard.NewEvents);
        Assert.Contains("마지막 확인 이후", dashboard.NewCountText);
    }

    [Fact]
    public async Task Event_explorer_searches_groups_details_notes_and_exports()
    {
        await using var h = await AppHarness.CreateAsync();
        var events = h.Main.Events;
        await events.OnActivatedAsync();
        Assert.NotEmpty(events.Events);

        events.SearchText = "4101";
        await events.SearchCommand.ExecuteAsync(null);
        Assert.Equal(5, events.Events.Count);
        events.SelectedEvent = events.Events[0];
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True(events.HasDetail);
        Assert.Contains(events.DetailFields, f => f.Name == "공급자" && f.Value == "Display");
        Assert.NotEmpty(events.DetailCaution);
        events.NoteText = "드라이버 롤백 후 다시 볼 것";
        await events.SaveNoteCommand.ExecuteAsync(null);
        Assert.Equal("드라이버 롤백 후 다시 볼 것", h.Engine.Store.GetNote(WinDiag.Core.Records.NoteTargets.Group, events.SelectedEvent!.GroupKey)!.Text);

        events.SearchText = "";
        events.GroupMode = true;
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Contains(events.Groups, g => g.Count == 5);
        await events.ShowGroupEventsCommand.ExecuteAsync(events.Groups.First(g => g.Count == 5));
        Assert.False(events.GroupMode);
        Assert.Equal(5, events.Events.Count);

        h.Dialogs.ConfirmAnswer = false; // minimal export
        await events.ExportCsvCommand.ExecuteAsync(null);
        var csv = Directory.GetFiles(h.Folder, "events-*.csv").Single();
        Assert.DoesNotContain(",message,", File.ReadAllText(csv));
        h.Dialogs.ConfirmAnswer = true; // detailed export, confirmed
        await events.ExportJsonCommand.ExecuteAsync(null);
        Assert.Contains("\"message\"", File.ReadAllText(Directory.GetFiles(h.Folder, "events-*.json").Single()));
    }

    [Fact]
    public async Task Hardware_page_shows_sensors_around_an_event()
    {
        await using var h = await AppHarness.CreateAsync();
        var tdr = h.Engine.Store.QueryEvents(new WinDiag.Storage.EventQuery { Providers = ["Display"], Limit = 1 }).Single();
        h.Main.ShowHardwareAt(tdr.TimeCreated, tdr.Title);
        await h.Main.Hardware.OnActivatedAsync();
        Assert.Same(h.Main.Hardware, h.Main.CurrentPage);
        Assert.True(h.Main.Hardware.IsFocused);
        Assert.NotNull(h.Main.Hardware.PowerChart);
        Assert.False(h.Main.Hardware.PowerChart!.Model.IsEmpty);
        Assert.NotEmpty(h.Main.Hardware.PowerChart.Markers);
        Assert.Contains(h.Main.Hardware.FocusStats, s => s.Metric.StartsWith("GPU 전력", StringComparison.Ordinal));
        Assert.NotEmpty(h.Main.Hardware.Capabilities);
        Assert.NotEmpty(h.Main.Hardware.Info);
    }

    [Fact]
    public async Task Navigation_waits_for_the_first_load_of_each_page()
    {
        // Setting CurrentPage starts the page's first load without waiting; whatever is awaited right after it must see
        // that load finished (this raced on a slow CI runner before the first load was shared).
        await using var h = await AppHarness.CreateAsync();
        h.Main.CurrentPage = h.Main.Hardware;
        await h.Main.Hardware.OnActivatedAsync();
        Assert.NotEmpty(h.Main.Hardware.Capabilities);
        Assert.NotEmpty(h.Main.Hardware.Info);

        // An informational event is not in the default list (warnings and above), so only the opened row can show it.
        var info = h.Engine.Store.QueryEvents(new WinDiag.Storage.EventQuery { Limit = 5000 }).First(e => e.Severity == WinDiag.Core.Events.DiagSeverity.Info);
        h.Main.CurrentPage = h.Main.Events; // what ShowEvent does, with the open awaited
        await h.Main.Events.OpenEventAsync(info.Id);
        Assert.Equal(info.Id, h.Main.Events.SelectedEvent?.Id);
        Assert.Contains(h.Main.Events.Events, r => r.Id == info.Id);

        h.Main.CurrentPage = h.Main.Diagnostics;
        await h.Main.Diagnostics.OnActivatedAsync();
        Assert.NotNull(h.Main.Diagnostics.Report);

        h.Main.ShowTimelineAround(info.TimeCreated);
        await h.Main.Timeline.OnActivatedAsync();
        Assert.NotEmpty(h.Main.Timeline.Items);
    }

    [Fact]
    public async Task Timeline_records_user_changes_and_makes_baselines()
    {
        await using var h = await AppHarness.CreateAsync();
        var timeline = h.Main.Timeline;
        await timeline.RefreshAsync();
        Assert.NotEmpty(timeline.Items);
        Assert.Contains(timeline.Items, i => i.IsUserInput);

        timeline.NewTitle = "메모리 전압 변경";
        timeline.NewKind = timeline.RecordKinds.First(k => k.Value == WinDiag.Core.Records.UserRecordKinds.MemoryVoltage);
        timeline.NewTime = "bad";
        await timeline.SaveRecordCommand.ExecuteAsync(null);
        Assert.Contains("시각", timeline.StatusText);
        timeline.NewDate = DateTime.Today.AddDays(-1); // a past time, whatever the clock of the test machine says
        timeline.NewTime = "10:30";
        timeline.NewValueName = "VDD";
        timeline.NewValueBefore = "1.35";
        timeline.NewValueAfter = "1.40";
        timeline.NewValueUnit = "V";
        timeline.AddValueCommand.Execute(null);
        await timeline.SaveRecordCommand.ExecuteAsync(null);
        var record = h.Engine.Store.ListUserRecords().Single(r => r.Title == "메모리 전압 변경");
        Assert.Equal("1.40", record.Values.Single().After);

        timeline.SelectedItem = timeline.Items.First(i => i.UserRecordId == record.Id);
        await timeline.CreateBaselineCommand.ExecuteAsync(null);
        Assert.Contains(h.Engine.Store.ListBaselines(), b => b.Name == "테스트 기준선");

        h.Dialogs.ConfirmAnswer = false;
        await timeline.DeleteSelectedRecordCommand.ExecuteAsync(null);
        Assert.Contains(h.Engine.Store.ListUserRecords(), r => r.Id == record.Id);
        h.Dialogs.ConfirmAnswer = true;
        await timeline.DeleteSelectedRecordCommand.ExecuteAsync(null);
        Assert.DoesNotContain(h.Engine.Store.ListUserRecords(), r => r.Id == record.Id);
    }

    [Fact]
    public async Task Diagnostics_page_runs_the_report_and_exports_it()
    {
        await using var h = await AppHarness.CreateAsync();
        var diagnostics = h.Main.Diagnostics;
        await diagnostics.RunAsync();
        Assert.True(diagnostics.HasReport);
        Assert.NotEmpty(diagnostics.Facts);
        Assert.NotEmpty(diagnostics.Associations);
        Assert.NotEmpty(diagnostics.Hypotheses);
        Assert.All(diagnostics.Hypotheses, x => Assert.True(x.HasVerification));
        Assert.Equal(2, diagnostics.Crashes.Count);
        Assert.True(diagnostics.HasCrashDetail);
        Assert.NotEmpty(diagnostics.CrashFacts);
        Assert.True(diagnostics.IncludesVirtual);

        await diagnostics.ExportHtmlCommand.ExecuteAsync(null);
        await diagnostics.ExportMarkdownCommand.ExecuteAsync(null);
        Assert.Single(Directory.GetFiles(h.Folder, "wdl-report-*.html"));
        Assert.Contains("원인 후보", File.ReadAllText(Directory.GetFiles(h.Folder, "wdl-report-*.md").Single()));
    }

    [Fact]
    public async Task Compare_page_compares_baselines_and_snapshots()
    {
        await using var h = await AppHarness.CreateAsync();
        var compare = h.Main.Compare;
        await compare.OnActivatedAsync();
        Assert.Equal(2, compare.Baselines.Count);
        compare.SelectedBaseline = compare.Baselines.Single(b => b.Name.Contains("드라이버", StringComparison.Ordinal));
        await compare.CompareCommand.ExecuteAsync(null);
        Assert.True(compare.HasResult);
        Assert.Contains(compare.Rates, r => r.Name == "GPU 드라이버 오류·재설정" && r.Change == "새로 발생");
        Assert.NotEmpty(compare.OtherChanges);
        Assert.NotEmpty(compare.Notes);
        await compare.SaveResultCommand.ExecuteAsync(null);
        Assert.Single(h.Engine.Store.ListComparisons());
        foreach (var format in new[] { "html", "csv", "json", "md" })
        {
            await compare.ExportResultCommand.ExecuteAsync(format);
        }

        Assert.Equal(4, Directory.GetFiles(h.Folder, "wdl-compare-*").Length);
        Assert.Equal(2, compare.Snapshots.Count);
        await compare.CompareSnapshotsCommand.ExecuteAsync(null);
        Assert.NotEmpty(compare.SnapshotDiffRows);
    }

    [Fact]
    public async Task Settings_validate_numbers_save_apply_and_back_up()
    {
        await using var h = await AppHarness.CreateAsync(demo: false);
        var settings = h.Main.Settings;
        settings.PollSeconds = "abc";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("숫자가 아닌 값", settings.StatusText);
        Assert.False(File.Exists(h.SettingsStore.Path));

        settings.PollSeconds = "30";
        settings.HistoryDays = "90";
        settings.UseUtc = true;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(30, h.SettingsStore.Load().Settings.Collection.PollSeconds);
        Assert.Equal(90, h.Engine.Settings.Collection.HistoryDays);
        Assert.True(Fmt.Display.IsUtc);
        Fmt.Display = WinDiag.Core.Time.TimeDisplay.Local;

        await settings.BackupCommand.ExecuteAsync(null);
        var backup = Directory.GetFiles(h.Folder, "wdl-backup-*.db").Single();
        Assert.True(WinDiag.Storage.DiagnosticsStore.ValidateBackup(backup).IsValid);

        h.Dialogs.ConfirmAnswer = false;
        await settings.LoadDemoDataCommand.ExecuteAsync(null);
        Assert.Equal(0, h.Engine.Store.GetStats().VirtualEvents);
        await settings.OnActivatedAsync();
        Assert.NotEmpty(settings.DatabaseInfo);
    }

    [Fact]
    public async Task Restore_checks_the_backup_and_asks_before_handing_it_to_the_app()
    {
        // The app side (stop, swap with the current database kept aside, restart) is DiagnosticsStore.RestoreFrom, tested
        // in the storage tests; the restart itself is not covered by automated tests.
        await using var h = await AppHarness.CreateAsync(demo: false);
        var settings = h.Main.Settings;

        h.Dialogs.OpenAnswer = Path.Combine(h.Folder, "not-a-database.db");
        await File.WriteAllTextAsync(h.Dialogs.OpenAnswer, "this is not SQLite", TestContext.Current.CancellationToken);
        await settings.RestoreCommand.ExecuteAsync(null);
        Assert.Contains("error:복원할 수 없음", h.Dialogs.Log);
        Assert.Empty(h.Host.Restores);

        h.Dialogs.ConfirmAnswer = true;
        await settings.BackupCommand.ExecuteAsync(null);
        var backup = Directory.GetFiles(h.Folder, "wdl-backup-*.db").Single();
        h.Dialogs.OpenAnswer = backup;
        h.Dialogs.ConfirmAnswer = false;
        await settings.RestoreCommand.ExecuteAsync(null);
        Assert.Empty(h.Host.Restores);

        h.Dialogs.ConfirmAnswer = true;
        await settings.RestoreCommand.ExecuteAsync(null);
        Assert.Equal([backup], h.Host.Restores);
    }
}
