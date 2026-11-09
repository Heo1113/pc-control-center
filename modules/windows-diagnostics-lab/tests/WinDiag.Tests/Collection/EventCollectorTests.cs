using WinDiag.Collection;
using WinDiag.Collection.Sources;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Storage;

namespace WinDiag.Tests.Collection;

public class EventCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IDisposable
    {
        public Harness(MemoryEventSource? source = null, AppSettings? settings = null, string? path = null)
        {
            Dir = path is null ? new TempDir() : null;
            Path = path ?? Dir!.File("wdl.db");
            Time = new ManualTime(Now);
            Source = source ?? new MemoryEventSource("test", isVirtual: false);
            Settings = settings ?? DefaultSettings();
            Store = DiagnosticsStore.Open(Path, Time);
            Collector = new EventCollector(Source, Store, EventClassifier.Default, new PrivacyMasker("kimtester", "DESKTOP-TEST01", null), () => Settings, Time);
        }

        public TempDir? Dir { get; }
        public string Path { get; }
        public ManualTime Time { get; }
        public MemoryEventSource Source { get; }
        public AppSettings Settings { get; }
        public DiagnosticsStore Store { get; }
        public EventCollector Collector { get; }

        public void Dispose()
        {
            Store.Dispose();
            Dir?.Dispose();
        }
    }

    private static AppSettings DefaultSettings()
    {
        var settings = new AppSettings();
        settings.Collection.Channels = [new ChannelSetting { Name = "System" }, new ChannelSetting { Name = "Application" }];
        settings.Normalize();
        return settings;
    }

    private static RawEvent At(string provider, int id, int level, DateTimeOffset time, string channel = "System", params (string?, string)[] data)
        => Ev.Make(provider, id, level, time, channel, record: 0, data: data);

    [Fact]
    public async Task First_scan_reads_the_history_window_and_filters_noise()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(At("disk", 7, 2, Now.AddDays(-40)));                       // older than 30 days
        source.Add(At("disk", 7, 2, Now.AddDays(-3)));                        // error: collected
        source.Add(At("Some-Noisy-Provider", 1, 4, Now.AddDays(-2)));          // info of unknown provider: not collected
        source.Add(At("Some-Noisy-Provider", 2, 3, Now.AddDays(-2)));          // warning of unknown provider: not collected
        source.Add(At("EventLog", 6005, 4, Now.AddDays(-1)));                 // catalogued info: collected
        source.Add(At("Microsoft-Windows-DNS-Client", 1014, 2, Now.AddDays(-1))); // excluded provider
        source.Add(At("Application Error", 1000, 2, Now.AddHours(-1), "Application", ("AppName", "a.exe")));
        using var h = new Harness(source);

        var report = await h.Collector.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, report.Inserted);
        Assert.Equal(EventSources.History, h.Store.QueryEvents(new EventQuery()).First().Source);
        var system = h.Store.GetChannelState("System")!;
        Assert.Equal(ChannelStatus.Ok, system.Status);
        Assert.Equal(6, system.LastRecordId); // scanned through the newest record, matching or not
        // System still holds a record older than the 30-day window; Application does not, so only it has a retention gap.
        Assert.DoesNotContain(h.Store.ListGaps(), g => g.Kind == GapKinds.LogRetention && g.Scope == "System");
        Assert.Contains(h.Store.ListGaps(), g => g.Kind == GapKinds.LogRetention && g.Scope == "Application");
    }

    [Fact]
    public async Task Later_polls_read_only_new_records_as_live_events()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(At("disk", 7, 2, Now.AddDays(-1)));
        source.EnsureChannel("Application");
        using var h = new Harness(source);
        StoredBatch? live = null;
        h.Collector.EventsStored += b => live = b;
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        Assert.False(live!.IsLive);

        source.Add(At("disk", 11, 2, Now.AddMinutes(1)));
        var report = await h.Collector.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Inserted);
        Assert.Equal(1, report.Read);
        Assert.True(live!.IsLive);
        Assert.Equal(EventSources.Live, h.Store.GetEvent(live.EventIds[0])!.Source);
    }

    [Fact]
    public async Task Restart_continues_from_the_saved_position_without_duplicates()
    {
        using var dir = new TempDir();
        var source = new MemoryEventSource("test", false);
        for (var i = 0; i < 1200; i++)
        {
            source.Add(At("disk", 153, 3, Now.AddMinutes(-1200 + i)));
        }

        source.EnsureChannel("Application");
        using (var first = new Harness(source, path: dir.File("wdl.db")))
        {
            // The app dies after 700 events: the first full batch (500) is committed with its position.
            source.FailNextRead = (700, new IOException("앱 강제 종료 시뮬레이션"));
            var crashed = await first.Collector.PollAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChannelStatus.Error, crashed.Channels[0].Status);
        }

        using var second = new Harness(source, path: dir.File("wdl.db"));
        var resumed = await second.Collector.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1200, second.Store.CountEvents(new EventQuery()));
        Assert.Equal(0, resumed.Duplicates);
        Assert.Equal(ChannelStatus.Ok, second.Store.GetChannelState("System")!.Status);
        var again = await second.Collector.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.Read);
    }

    [Fact]
    public async Task Access_denied_channel_is_reported_once_and_others_continue()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(At("disk", 7, 2, Now.AddDays(-1)));
        source.SetAccess("Application", ChannelAccess.AccessDenied, "Access is denied");
        using var h = new Harness(source);

        var report = await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelStatus.AccessDenied, report.Channels.Single(c => c.Channel == "Application").Status);
        Assert.Equal(1, report.Inserted);
        var state = h.Store.GetChannelState("Application")!;
        Assert.Equal(ChannelStatus.AccessDenied, state.Status);
        Assert.Contains("Event Log Readers", state.StatusMessage);
        Assert.Single(h.Store.ListGaps(), g => g.Kind == GapKinds.AccessDenied);
    }

    [Fact]
    public async Task Missing_and_disabled_channels_are_marked_not_failed()
    {
        var source = new MemoryEventSource("test", false);
        source.EnsureChannel("System");
        var settings = DefaultSettings();
        settings.Collection.Channels.Add(new ChannelSetting { Name = "Microsoft-Windows-Nope/Operational" });
        settings.Collection.Channels.Single(c => c.Name == "Application").Enabled = false;
        using var h = new Harness(source, settings);

        var report = await h.Collector.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelStatus.NotFound, report.Channels.Single(c => c.Channel.StartsWith("Microsoft", StringComparison.Ordinal)).Status);
        Assert.Equal(ChannelStatus.Disabled, h.Store.GetChannelState("Application")!.Status);
        Assert.Empty(h.Store.ListGaps());
    }

    [Fact]
    public async Task Overwritten_records_and_recreated_logs_become_gaps()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(At("disk", 7, 2, Now.AddHours(-5)));
        source.EnsureChannel("Application");
        using var h = new Harness(source);
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);

        // The log wraps: records 2..10 are written and dropped before the app reads them.
        for (var i = 0; i < 10; i++)
        {
            source.Add(At("disk", 7, 2, Now.AddHours(-4).AddMinutes(i)));
        }

        source.DropOldest("System", 9);
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        Assert.Contains(h.Store.ListGaps(), g => g.Kind == GapKinds.LogOverwritten);

        // The log is recreated: numbering starts again at 1.
        source.Clear("System", resetRecordIds: true);
        source.Add(At("disk", 51, 2, Now.AddHours(-1)));
        var afterReset = await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        Assert.Contains(h.Store.ListGaps(), g => g.Kind == GapKinds.LogReset);
        Assert.Equal(1, afterReset.Inserted);
        Assert.Equal(1, h.Store.GetChannelState("System")!.LastRecordId);
    }

    [Fact]
    public async Task Log_cleared_event_records_a_gap_once()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(Fixtures.Event("eventlog-104.xml") with { RecordId = 0, TimeCreated = Now.AddHours(-2) });
        source.EnsureChannel("Application");
        using var h = new Harness(source);
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        await h.Collector.ImportHistoryAsync(Now.AddDays(-2), Now, null, TestContext.Current.CancellationToken);
        Assert.Single(h.Store.ListGaps(), g => g.Kind == GapKinds.LogCleared);
        var stored = h.Store.QueryEvents(new EventQuery()).Single();
        Assert.DoesNotContain("kimtester", stored.Data.Select(d => d.Value));
    }

    [Fact]
    public async Task History_import_extends_the_range_without_moving_the_live_position()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(At("disk", 7, 2, Now.AddDays(-60)));
        source.Add(At("disk", 7, 2, Now.AddDays(-1)));
        source.EnsureChannel("Application");
        using var h = new Harness(source);
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        var position = h.Store.GetChannelState("System")!.LastRecordId;

        var report = await h.Collector.ImportHistoryAsync(Now.AddDays(-90), Now, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Inserted);
        Assert.Equal(1, report.Duplicates);
        Assert.Equal(position, h.Store.GetChannelState("System")!.LastRecordId);
        Assert.Equal(Now.AddDays(-90), h.Store.GetChannelState("System")!.HistoryFrom);
    }

    [Fact]
    public async Task Masking_can_be_switched_off_but_grouping_stays_stable()
    {
        var source = new MemoryEventSource("test", false);
        source.Add(Fixtures.Event("app-error-1000.xml") with { RecordId = 0, TimeCreated = Now.AddHours(-1) });
        source.EnsureChannel("System");
        var settings = DefaultSettings();
        settings.Privacy.MaskAtCollection = false;
        using var h = new Harness(source, settings);
        await h.Collector.PollAsync(TestContext.Current.CancellationToken);
        var stored = h.Store.QueryEvents(new EventQuery()).Single();
        Assert.False(stored.IsMasked);
        Assert.Contains(stored.Data, d => d.Value.Contains("kimtester", StringComparison.Ordinal));
        Assert.Equal(EventClassifier.Default.Classify(new PrivacyMasker("kimtester", "DESKTOP-TEST01", null).Mask(Fixtures.Event("app-error-1000.xml"))).GroupKey, stored.GroupKey);
    }

    [Fact]
    public async Task Virtual_scenario_is_stored_as_virtual_data()
    {
        var source = VirtualScenario.BuildSource(Now);
        using var h = new Harness(source);
        h.Settings.Collection.Channels.Add(new ChannelSetting { Name = "Microsoft-Windows-Kernel-PnP/Configuration" });
        await h.Collector.ImportHistoryAsync(Now.AddDays(-31), Now, null, TestContext.Current.CancellationToken);
        var events = h.Store.QueryEvents(new EventQuery { Limit = 10_000 });
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.True(e.IsVirtual));
        Assert.All(events, e => Assert.StartsWith(VirtualScenario.Marker, e.Message));
        Assert.Equal(5, events.Count(e => e.Provider == "Display" && e.EventId == 4101));
        Assert.Equal(2, events.Count(e => e.Category == EventCategories.UnexpectedShutdown && e.EventId == 41));
        Assert.Single(events, e => e.Category == EventCategories.BugCheck);
        Assert.Contains(events, e => e.Category == EventCategories.DriverInstall && e.Channel.Contains("Kernel-PnP", StringComparison.Ordinal));
    }
}
