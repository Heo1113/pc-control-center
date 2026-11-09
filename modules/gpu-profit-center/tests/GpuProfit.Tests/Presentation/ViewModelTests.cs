using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Core.Settings;
using GpuProfit.Presentation;
using GpuProfit.Presentation.ViewModels;
using GpuProfit.Recording;
using GpuProfit.Storage;
using LocalAiPerf.Telemetry;

namespace GpuProfit.Tests.Presentation;

internal sealed class FakeDialogs(string folder) : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public string? NextOpen { get; set; }
    public List<string> Log { get; } = [];

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        Log.Add("save:" + defaultFileName);
        return Path.Combine(folder, defaultFileName);
    }

    public string? OpenFile(string title, string filter, string? initialDirectory = null)
    {
        Log.Add("open:" + title);
        return NextOpen;
    }

    public bool Confirm(string title, string message)
    {
        Log.Add("confirm:" + title);
        return ConfirmAnswer;
    }

    public void Info(string title, string message) => Log.Add("info:" + message);

    public void Error(string title, string message) => Log.Add("error:" + message);
}

internal sealed class FakeShell : IShellService
{
    public void OpenFolder(string path)
    {
    }
}

internal sealed class FakeHost : IAppHost
{
    public string? Restored { get; private set; }
    public double Scale { get; private set; } = 1;

    public Task RestoreAndRestartAsync(string backupPath)
    {
        Restored = backupPath;
        return Task.CompletedTask;
    }

    public void ApplyFontScale(double scale) => Scale = scale;
}

/// <summary>Every screen's view model against a real engine (scripted mock GPUs, manual clock, SQLite file). No WPF.</summary>
public class ViewModelTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Dir = new TempDir();
            Time = new ManualTime(DateTimeOffset.UtcNow.AddHours(-3));
            var monitor = new HardwareMonitor(new ScriptedGpuProvider(Time, _ => 320.0, _ => 180.0), new FixedSystemProvider(), Time, isMock: true);
            Engine = ProfitEngine.Start(new EngineOptions { Paths = new AppPaths(Dir.Path, false), Monitor = monitor, Time = Time, AutoSample = false });
            Dialogs = new FakeDialogs(Dir.Path);
            Host = new FakeHost();
            Context = new ViewContext
            {
                Engine = Engine,
                SettingsStore = new SettingsStore(Path.Combine(Dir.Path, "settings.json")),
                Ui = new InlineDispatcher(),
                Dialogs = Dialogs,
                Shell = new FakeShell(),
                Host = Host,
                Time = Time,
            };
            Main = new MainViewModel(Context);
        }

        public TempDir Dir { get; }
        public ManualTime Time { get; }
        public ProfitEngine Engine { get; }
        public FakeDialogs Dialogs { get; }
        public FakeHost Host { get; }
        public ViewContext Context { get; }
        public MainViewModel Main { get; }

        public void Run(int seconds)
        {
            for (var i = 0; i < seconds; i += 5)
            {
                Time.Advance(TimeSpan.FromSeconds(Math.Min(5, seconds - i)));
                Engine.Recorder!.SampleOnce();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Main.Dispose();
            await Engine.StopAsync();
            Dir.Dispose();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Every_page_activates_on_an_empty_database_with_empty_state_texts()
    {
        await using var h = new Harness();
        h.Engine.Recorder!.SampleOnce();
        foreach (var page in h.Main.Pages)
        {
            h.Main.CurrentPage = page;
            await page.OnActivatedAsync();
            Assert.DoesNotContain("오류", page.StatusText);
        }

        Assert.Equal(8, h.Main.Pages.Count);
        Assert.Contains("작업이 없습니다", h.Main.Dashboard.EmptyText);
        Assert.NotEmpty(h.Main.Sessions.EmptyText);
        Assert.NotEmpty(h.Main.Profitability.EmptyText);
        Assert.NotEmpty(h.Main.Compare.EmptyText);
        Assert.NotEmpty(h.Main.History.EmptyText);
    }

    [Fact(Timeout = 120_000)]
    public async Task Record_account_compare_and_export_through_the_view_models()
    {
        await using var h = new Harness();
        h.Engine.Recorder!.SampleOnce();
        var sessions = h.Main.Sessions;
        h.Main.CurrentPage = sessions;
        await sessions.OnActivatedAsync();

        // Work item and session (GPU 0 only).
        sessions.NewWorkloadName = "AI 추론 테스트";
        sessions.NewWorkloadType = sessions.Types.First(t => t.Value == WorkloadType.AiInference);
        await sessions.AddWorkloadCommand.ExecuteAsync(null);
        Assert.Single(sessions.Workloads);
        sessions.StartTitle = "첫 세션";
        sessions.StartGpus = "0";
        await sessions.StartCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(sessions.Sessions).IsOpen);
        h.Run(1800);
        await sessions.PauseCommand.ExecuteAsync(null);
        h.Run(300);
        await sessions.ResumeCommand.ExecuteAsync(null);
        h.Run(1800);
        await sessions.StopCommand.ExecuteAsync(null);
        Assert.Contains("종료", sessions.StatusText);
        Assert.Equal(2, sessions.Segments.Count);
        Assert.Single(sessions.Devices); // GPU 0 only
        Assert.NotNull(sessions.PowerChart);
        Assert.False(sessions.PowerChart!.IsEmpty);

        // Session settings: confirm execution, add an extra-system assumption.
        sessions.EditConfirmed = true;
        sessions.EditBasis = sessions.Bases.First(b => b.Value == EnergyBasis.GpuPlusSystemAssumption);
        sessions.EditExtraW = "80";
        await sessions.SaveDetailsCommand.ExecuteAsync(null);
        Assert.Contains("저장", sessions.StatusText);
        var id = sessions.Sessions[0].Id;
        Assert.True(h.Engine.Store.GetSession(id)!.ExecutionConfirmed);

        // Profitability: revenue in USD without a rate → undefined; then add a rate.
        var p = h.Main.Profitability;
        h.Main.ShowProfitability(id);
        await p.OnActivatedAsync();
        Assert.Contains(p.Headline, r => r.Name == "매출" && r.Value.StartsWith("N/A"));
        p.RevAmount = "3";
        p.RevCurrency = "usd";
        p.RevOccurred = Fmt.InputTime(h.Time.GetUtcNow());
        await p.AddRevenueCommand.ExecuteAsync(null);
        Assert.Contains(p.Headline, r => r.Name == "매출" && r.Value.Contains("USD"));
        var settings = h.Main.Settings;
        settings.RateCurrency = "USD";
        settings.RateValue = "1400";
        settings.RateAsOf = Fmt.InputTime(h.Time.GetUtcNow().AddDays(-1));
        await settings.AddRateCommand.ExecuteAsync(null);
        Assert.Single(settings.Rates);
        await p.RefreshAsync();
        Assert.Contains(p.Headline, r => r.Name == "매출" && r.Value == "4,200 원");

        // Costs and fee, pending revenue marked received.
        p.SelectedCostBasis = p.CostBases.First(b => b.Value == CostBasis.PercentOfRevenue);
        p.CostScope = p.CostScopes[2];
        p.CostAmount = "2";
        await p.AddCostCommand.ExecuteAsync(null);
        p.RevKind = p.RevenueKinds.First(k => k.Value == RevenueKind.Pending);
        p.RevAmount = "500";
        p.RevCurrency = "KRW";
        await p.AddRevenueCommand.ExecuteAsync(null);
        Assert.Equal(2, p.Revenue.Count);
        p.SelectedRevenue = p.Revenue.First(r => r.Kind == RevenueKindText.ToKorean(RevenueKind.Pending));
        await p.MarkReceivedCommand.ExecuteAsync(null);
        Assert.All(h.Engine.Store.ListRevenue(id), e => Assert.Equal(RevenueKind.Received, e.Kind));
        await p.SaveResultCommand.ExecuteAsync(null);
        Assert.NotEmpty(p.History);
        Assert.Contains("같습니다", p.StaleText);
        Assert.Contains(p.Breakdown, b => b.Origin == "추정·가정"); // the system assumption

        // Payout import assigned by time.
        var csv = Path.Combine(h.Dir.Path, "payouts.csv");
        await File.WriteAllTextAsync(csv, $"time,amount,currency,id\n{h.Time.GetUtcNow().AddMinutes(-10):yyyy-MM-ddTHH:mm:ssZ},1000,KRW,p1\n", TestContext.Current.CancellationToken);
        h.Dialogs.NextOpen = csv;
        p.ImportProvider = "Test Pool";
        await p.ImportCommand.ExecuteAsync(null);
        Assert.Contains("1건 추가", p.StatusText);
        await p.ImportCommand.ExecuteAsync(null);
        Assert.Contains("중복 1건", p.StatusText);

        // A second session and comparison.
        h.Run(10);
        await sessions.StartCommand.ExecuteAsync(null);
        h.Run(1200);
        await sessions.StopCommand.ExecuteAsync(null);
        var compare = h.Main.Compare;
        await compare.LoadCandidatesAsync();
        Assert.Equal(2, compare.Candidates.Count);
        await compare.CompareCommand.ExecuteAsync(null);
        Assert.Equal(2, compare.Rows.Count);
        Assert.Contains(compare.Warnings, w => w.Contains("MOCK"));
        Assert.NotEmpty(compare.ByTimeOfDay);
        await compare.ExportCommand.ExecuteAsync(null);
        Assert.Single(Directory.GetFiles(h.Dir.Path, "gpc-compare-*.csv"));

        // Scenarios from the session, variants, sensitivity.
        var sc = h.Main.Scenarios;
        await sc.OnActivatedAsync();
        sc.SourceSession = sc.SessionOptions.First(o => o.Value == id);
        await sc.FromSessionCommand.ExecuteAsync(null);
        Assert.Equal("320", sc.GpuPower);
        await sc.SaveCommand.ExecuteAsync(null);
        await sc.VariantsCommand.ExecuteAsync(null);
        Assert.Equal(3, sc.Scenarios.Count);
        Assert.Equal(3, sc.Comparison.Count);
        Assert.NotNull(sc.SensitivityChart);
        Assert.StartsWith("손익분기점", sc.BreakevenText);
        Assert.Equal(7, sc.Tornado.Count);
        Assert.Contains("바뀌지 않았습니다", sc.TrackingText);

        // History and dashboard totals.
        var hist = h.Main.History;
        await hist.OnActivatedAsync();
        Assert.NotEmpty(hist.Periods);
        Assert.False(hist.PowerChart!.IsEmpty);
        await hist.ExportCommand.ExecuteAsync(null);
        await h.Main.Dashboard.OnActivatedAsync();
        Assert.Contains(h.Main.Dashboard.Totals, t => t.Period == "오늘" && t.Sessions != "0");

        // Settings: profile change → recalculation of history; backup and restore request.
        settings.FlatRate = "300";
        await settings.SaveProfileCommand.ExecuteAsync(null);
        Assert.Contains("다시 계산", settings.StatusText);
        Assert.True(h.Engine.Store.ListResults(id).Count >= 2);
        await settings.BackupCommand.ExecuteAsync(null);
        var backup = Assert.Single(Directory.GetFiles(h.Dir.Path, "gpc-backup-*.db"));
        Assert.True(ProfitStore.ValidateBackup(backup).IsValid);
        h.Dialogs.NextOpen = backup;
        await settings.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(backup, h.Host.Restored);
        settings.FontScale = settings.FontScales[2];
        await settings.SaveAppSettingsCommand.ExecuteAsync(null);
        Assert.Equal(1.3, h.Host.Scale);
        await settings.ExportLedgerCommand.ExecuteAsync(null);
        await settings.ExportSessionsCommand.ExecuteAsync(null);
        Assert.Single(Directory.GetFiles(h.Dir.Path, "gpc-ledger-*.json"));
        Assert.DoesNotContain(h.Main.Pages, page => page.StatusText.StartsWith("오류", StringComparison.Ordinal));
    }

    [Fact(Timeout = 120_000)]
    public async Task Input_errors_are_reported_not_thrown()
    {
        await using var h = new Harness();
        var sessions = h.Main.Sessions;
        await sessions.OnActivatedAsync();
        await sessions.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("입력 확인", sessions.StatusText);
        sessions.NewWorkloadName = "w";
        await sessions.AddWorkloadCommand.ExecuteAsync(null);
        sessions.StartGpus = "abc";
        await sessions.StartCommand.ExecuteAsync(null);
        Assert.Contains("GPU 번호", sessions.StatusText);
        sessions.ManualStart = "어제";
        await sessions.CreateManualCommand.ExecuteAsync(null);
        Assert.Contains("yyyy-MM-dd", sessions.StatusText);
        sessions.NewWorkloadNotes = "-----BEGIN PRIVATE KEY-----";
        sessions.NewWorkloadName = "secret";
        await sessions.AddWorkloadCommand.ExecuteAsync(null);
        Assert.Contains("개인키", sessions.StatusText);

        var settings = h.Main.Settings;
        settings.MaxGap = "0";
        await settings.SaveProfileCommand.ExecuteAsync(null);
        Assert.Contains("최대 샘플 간격", settings.StatusText);
        settings.TiersText = "abc";
        Assert.Throws<ArgumentException>(() => SettingsViewModel.ParseTiers("abc"));
        Assert.Equal(3, SettingsViewModel.ParseTiers("200,120,910\n400,215,1600\n,307,7300").Count);

        var sc = h.Main.Scenarios;
        sc.Utilization = "150";
        await sc.CalculateCommand.ExecuteAsync(null);
        Assert.Contains("가동률", sc.StatusText);
    }

    [Fact(Timeout = 120_000)]
    public async Task Cancelled_confirmation_changes_nothing()
    {
        await using var h = new Harness();
        var sessions = h.Main.Sessions;
        await sessions.OnActivatedAsync();
        sessions.NewWorkloadName = "w";
        await sessions.AddWorkloadCommand.ExecuteAsync(null);
        await sessions.StartCommand.ExecuteAsync(null);
        h.Run(30);
        await sessions.StopCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmAnswer = false;
        await sessions.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("취소했습니다.", sessions.StatusText);
        Assert.Single(h.Engine.Store.ListSessions(new SessionQuery()));
    }

    [Fact(Timeout = 120_000)]
    public async Task Demo_data_is_flagged_and_shown()
    {
        await using var h = new Harness();
        await h.Main.Settings.AddDemoDataCommand.ExecuteAsync(null);
        Assert.Contains("6개", h.Main.Settings.StatusText);
        h.Main.Tick();
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Main.Tick();
        Assert.True(h.Main.HasDemoData);
        await h.Main.Sessions.OnActivatedAsync();
        Assert.All(h.Main.Sessions.Sessions, s => Assert.True(s.IsMock));
    }

    [Fact]
    public void Narrow_sensor_ranges_get_a_minimum_vertical_span()
    {
        // A temperature flipping between 30 and 31 °C (seen on a real RTX 5080 at idle) must not fill the whole chart height.
        var flat = new LocalAiPerf.Presentation.Charts.ChartModel
        {
            Title = "GPU 온도", Unit = "°C",
            Series = [new LocalAiPerf.Presentation.Charts.ChartSeries("GPU", [1, 2, 3, 4], [30, 31, 30, 31], "#3987e5")],
        };
        var widened = Charts.WithMinimumSpan(flat, 10);
        Assert.Equal(25, widened.YMin);
        Assert.Equal(35, widened.YMax);

        var low = Charts.WithMinimumSpan(flat with { Series = [new("GPU", [1, 2], [1, 2], "#3987e5")] }, 10);
        Assert.Equal(0, low.YMin); // never below zero for non-negative data

        var wide = flat with { Series = [new("GPU", [1, 2], [30, 70], "#3987e5")] };
        Assert.Null(Charts.WithMinimumSpan(wide, 10).YMin); // a real change keeps the automatic scale
        var fixedAxis = flat with { YMin = 0, YMax = 100 };
        Assert.Equal(100, Charts.WithMinimumSpan(fixedAxis, 10).YMax);
    }
}
