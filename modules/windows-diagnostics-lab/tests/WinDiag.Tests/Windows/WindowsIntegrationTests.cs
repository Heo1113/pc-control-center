using System.Runtime.Versioning;
using LocalAiPerf.Telemetry.Nvml;
using WinDiag.Collection;
using WinDiag.Collection.Crash;
using WinDiag.Collection.Snapshots;
using WinDiag.Collection.Sources;
using WinDiag.Core;
using WinDiag.Core.Events;
using WinDiag.Core.Settings;
using WinDiag.Storage;

namespace WinDiag.Tests.Windows;

/// <summary>
/// Runs against the real Windows APIs of the machine running the tests (Windows CI runner). These verify that the
/// event log queries, WMI reads and missing-sensor handling work on Windows; they are skipped elsewhere.
/// The CI runner has no NVIDIA GPU and its logs are not the user's PC: results say nothing about RTX 5080 hardware.
/// The runner's account is an administrator, so these tests do not show behaviour under standard user rights.
/// </summary>
public class WindowsIntegrationTests
{
    private const string WindowsOnly = "Windows 전용 통합 테스트";

    private static CollectionSettings Collection()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings.Collection;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void System_log_can_be_probed_and_queried_with_the_generated_structured_query()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var source = new WindowsEventLogSource();
        var probe = source.Probe("System");
        Assert.Equal(ChannelAccess.Ok, probe.Access);
        Assert.NotNull(probe.NewestRecordId);

        var filter = EventFilter.Build("System", EventCatalog.Default, Collection());
        var events = source.Read(new EventReadRequest
        {
            Channel = "System", Filter = filter, From = DateTimeOffset.UtcNow.AddDays(-30), To = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken).Take(500).ToList();
        Assert.All(events, e => Assert.True(filter.Matches(e), $"{e.Provider} {e.EventId} level {e.Level}"));
        Assert.All(events, e => Assert.Equal("System", e.Channel));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Every_default_channel_query_is_accepted_by_the_event_log_service()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var source = new WindowsEventLogSource(renderMessages: false);
        foreach (var channel in CollectionSettings.DefaultChannels)
        {
            var probe = source.Probe(channel.Name);
            if (probe.Access != ChannelAccess.Ok)
            {
                continue;
            }

            var filter = EventFilter.Build(channel.Name, EventCatalog.Default, Collection());
            var request = new EventReadRequest { Channel = channel.Name, Filter = filter, AfterRecordId = Math.Max(0, (probe.NewestRecordId ?? 0) - 200) };
            _ = source.Read(request, TestContext.Current.CancellationToken).Take(50).ToList();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Missing_channel_is_reported_not_thrown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var probe = new WindowsEventLogSource().Probe("WindowsDiagnosticsLab-NoSuchChannel/Operational");
        Assert.Equal(ChannelAccess.NotFound, probe.Access);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Security_log_probe_returns_a_status_whatever_the_rights()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var probe = new WindowsEventLogSource().Probe("Security");
        Assert.Contains(probe.Access, new[] { ChannelAccess.Ok, ChannelAccess.AccessDenied });
        Assert.Equal(Environment.IsPrivilegedProcess ? ChannelAccess.Ok : ChannelAccess.AccessDenied, probe.Access);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Collector_reads_real_logs_into_the_store_and_resumes_without_duplicates()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var dir = new TempDir();
        var settings = new AppSettings();
        settings.Collection.HistoryDays = 7;
        settings.Normalize();
        using (var store = DiagnosticsStore.Open(dir.File("wdl.db")))
        {
            var collector = new EventCollector(new WindowsEventLogSource(), store, EventClassifier.Default,
                WinDiag.Core.Privacy.PrivacyMasker.ForCurrentMachine(), () => settings);
            var first = await collector.PollAsync(TestContext.Current.CancellationToken);
            Assert.Contains(first.Channels, c => c.Channel == "System" && c.Status == ChannelStatus.Ok);
            var second = await collector.PollAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, second.Duplicates);
        }

        using var reopened = DiagnosticsStore.Open(dir.File("wdl.db"));
        Assert.NotNull(reopened.GetChannelState("System")!.LastRecordId);
        var masked = reopened.QueryEvents(new EventQuery { Limit = 2000 });
        Assert.DoesNotContain(masked, e => (e.Message ?? "").Contains(Environment.UserName + "\\", StringComparison.OrdinalIgnoreCase)
                                           && Environment.UserName.Length >= 4);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Configuration_snapshot_reads_windows_version_and_hardware()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var snapshot = new WindowsSnapshotProvider(() => null).Collect(includeDrivers: true, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot.Os?.CurrentBuild);
        Assert.True(int.Parse(snapshot.Os!.CurrentBuild!, System.Globalization.CultureInfo.InvariantCulture) > 10000);
        Assert.NotEmpty(snapshot.Gpus);
        Assert.False(string.IsNullOrEmpty(snapshot.ContentHash()));
    }

    [Fact]
    public void Nvml_without_an_nvidia_driver_reports_a_reason_instead_of_values()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var provider = new NvmlGpuProvider();
        if (provider.IsAvailable)
        {
            Assert.NotEmpty(provider.Devices); // a machine with an NVIDIA GPU
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(provider.UnavailableReason));
        Assert.Empty(provider.ReadGpus(new List<LocalAiPerf.Core.Hardware.SensorIssue>()));
    }

    [Fact]
    public void Dump_scan_of_the_standard_locations_never_throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var result = new DumpScanner().Scan();
        Assert.NotNull(result);
        Assert.True(result.Files.Count > 0 || result.Notes.Count > 0);
    }

    [Fact]
    public async Task Engine_starts_on_windows_with_real_sources_and_stops_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var dir = new TempDir();
        var settings = new AppSettings();
        settings.Collection.HistoryDays = 3;
        settings.Normalize();
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = AppPaths.Resolve(dir.Path, dir.Path), Settings = settings, StartBackgroundLoops = false,
        });
        try
        {
            var report = await engine.PollNowAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(report);
            var snapshot = await engine.TakeSnapshotAsync("test", TestContext.Current.CancellationToken);
            Assert.True(snapshot.IsNew);
            Assert.NotNull(engine.Hardware.Info.CpuName);
        }
        finally
        {
            await engine.StopAsync();
        }
    }
}
