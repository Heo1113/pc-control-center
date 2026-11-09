using LocalAiPerf.Core;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.MockStrata;
using LocalAiPerf.Recording;
using LocalAiPerf.Storage;
using Xunit;

namespace LocalAiPerf.Tests.Recording;

public class MonitoringServiceTests
{
    [Fact]
    public async Task End_to_end_mock_sensors_and_mock_strata_are_recorded_and_closed_on_exit()
    {
        using var dir = new TempDir();
        await using var server = new MockStrataServer();
        server.Start();
        var settings = new AppSettings();
        settings.Sampling.IntervalMs = 500;
        settings.Strata.BaseUrl = server.BaseUrl;
        settings.Normalize();
        var paths = new AppPaths(dir.Path, false);
        long sessionId;

        await using (var service = new MonitoringService(paths, settings, new MonitoringOptions { UseMockSensors = true }))
        {
            var frames = 0;
            service.FrameCollected += _ => Interlocked.Increment(ref frames);
            await service.StartAsync();
            server.CompleteRequest(outputTokens: 300);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            sessionId = service.Recorder!.ActiveSession!.Id;
            Assert.True(frames >= 3, $"frames={frames}");
            Assert.NotNull(service.Live.Latest);
            Assert.Equal(RuntimeConnectionState.Connected, service.LatestRuntime?.State);
        }

        using var store = TelemetryStore.Open(paths.DatabasePath);
        var data = store.LoadSessionData(sessionId)!;
        Assert.Equal(SessionEndReasons.AppExit, data.Session.EndReason);
        Assert.True(data.Session.IsMock);
        Assert.True(data.System.Count >= 3);
        Assert.All(data.System, s => Assert.True(s.Flags.HasFlag(SampleFlags.Mock)));
        Assert.NotEmpty(data.Gpu);
        Assert.NotEmpty(data.Runtime);
        Assert.Contains(data.Runtime, r => r.State == RuntimeConnectionState.Connected && r.AnswerTokS is not null);
        Assert.Equal(server.ModelId, data.RuntimeInfo.First().Conditions.ModelId);
        Assert.DoesNotContain(server.Requests, r => r.Path == "/status" || r.Method != "GET");
    }

    [Fact]
    public async Task Hardware_monitoring_continues_when_strata_is_not_running()
    {
        using var dir = new TempDir();
        var settings = new AppSettings();
        settings.Sampling.IntervalMs = 500;
        settings.Strata.BaseUrl = "http://127.0.0.1:9"; // discard port: nothing listens
        settings.Normalize();

        await using var service = new MonitoringService(new AppPaths(dir.Path, false), settings, new MonitoringOptions { UseMockSensors = true });
        await service.StartAsync();
        for (var i = 0; i < 100 && (service.LatestRuntime is null || service.Live.Latest is null); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken); // a refused connect takes ~2 s on Windows
        }

        Assert.NotNull(service.Live.Latest);
        Assert.NotEmpty(service.Live.Latest!.Hardware.Gpus);
        Assert.Equal(RuntimeConnectionState.Unreachable, service.LatestRuntime?.State);
        Assert.Equal(RuntimeErrorCategory.Connection, service.LatestRuntime?.Error?.Category);
    }

    [Fact]
    public async Task Storage_that_cannot_be_opened_keeps_live_monitoring_without_recording()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, false);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE x (a INTEGER); PRAGMA user_version = 99;"; // written by a "newer" app
            command.ExecuteNonQuery();
        }

        var settings = new AppSettings();
        settings.Sampling.IntervalMs = 500;
        settings.Strata.Enabled = false;
        await using var service = new MonitoringService(paths, settings, new MonitoringOptions { UseMockSensors = true });
        await service.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        Assert.Null(service.Store);
        Assert.Null(service.Recorder);
        Assert.NotNull(service.StorageProblem);
        Assert.Contains(service.Notices, n => n.Contains("데이터베이스"));
        Assert.NotNull(service.Live.Latest);
    }
}
