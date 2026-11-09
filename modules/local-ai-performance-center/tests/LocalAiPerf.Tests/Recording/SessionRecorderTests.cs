using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Recording;
using LocalAiPerf.Storage;
using Xunit;
using static LocalAiPerf.Tests.FrameFactory;

namespace LocalAiPerf.Tests.Recording;

public class SessionRecorderTests
{
    private static SessionRecorder Recorder(TelemetryStore store, bool autoSplit = true, TimeProvider? time = null)
        => new(store, new RecorderOptions(1000, autoSplit, "strata", "http://127.0.0.1:8080", false), () => new HardwareInfo { CpuName = "cpu" }, time);

    [Fact]
    public async Task Frames_are_written_only_while_a_session_is_active()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        await using var recorder = Recorder(store);

        recorder.Enqueue(Frame(T0));
        var session = await recorder.StartAsync("run", SessionKind.Manual);
        recorder.Enqueue(Frame(T0.AddSeconds(1), seq: 1));
        recorder.Enqueue(Frame(T0.AddSeconds(2), seq: 2));
        await recorder.StopAsync();
        recorder.Enqueue(Frame(T0.AddSeconds(3), seq: 3));
        await recorder.FlushAsync();

        var data = store.LoadSessionData(session.Id)!;
        Assert.Equal(2, data.System.Count);
        Assert.Equal(SessionEndReasons.UserStopped, data.Session.EndReason);
        Assert.Single(data.RuntimeInfo);
    }

    [Fact]
    public async Task Auto_session_is_split_when_the_model_changes()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        await using var recorder = Recorder(store);

        var first = await recorder.StartAsync(null, SessionKind.Auto);
        recorder.Enqueue(Frame(T0, model: "model-a"));
        recorder.Enqueue(Frame(T0.AddSeconds(1), model: "model-a", seq: 1));
        recorder.Enqueue(Frame(T0.AddSeconds(2), model: "model-b", seq: 2));
        await recorder.FlushAsync();

        var second = recorder.ActiveSession!;
        Assert.NotEqual(first.Id, second.Id);
        var closed = store.GetSession(first.Id)!;
        Assert.Equal(SessionEndReasons.ConditionChanged, closed.EndReason);
        Assert.Equal(2, store.LoadSessionData(first.Id)!.System.Count);
        Assert.Equal("model-b", Assert.Single(store.LoadSessionData(second.Id)!.RuntimeInfo).Conditions.ModelId);
        Assert.Contains("model-a", second.Notes);
    }

    [Fact]
    public async Task Manual_session_is_not_split_but_records_the_change()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        await using var recorder = Recorder(store);

        var session = await recorder.StartAsync("manual", SessionKind.Manual);
        recorder.Enqueue(Frame(T0, engine: "0.1.54"));
        recorder.Enqueue(Frame(T0.AddSeconds(1), engine: "0.1.55", seq: 1));
        await recorder.FlushAsync();

        Assert.Equal(session.Id, recorder.ActiveSession!.Id);
        var data = store.LoadSessionData(session.Id)!;
        Assert.Equal(2, data.RuntimeInfo.Count);
        Assert.Contains(data.Events, e => e.Category == EventCategories.RuntimeChange && e.Message.Contains("0.1.54"));
    }

    [Fact]
    public async Task Requests_completed_before_the_session_are_not_attributed_to_it()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var time = new ManualTime(T0);
        await using var recorder = Recorder(store, time: time);
        var session = await recorder.StartAsync("run", SessionKind.Manual);
        var startUnix = T0.ToUnixTimeMilliseconds() / 1000.0;
        var before = new CompletedRequest { Id = "000000000001", StartedUnix = startUnix - 100, DurationSeconds = 10, OutputTokens = 50, DecodeMs = 1000 };
        var during = new CompletedRequest { Id = "000000000002", StartedUnix = startUnix + 1, DurationSeconds = 2, OutputTokens = 50, DecodeMs = 1000, Finish = "error" };

        recorder.Enqueue(Frame(T0.AddSeconds(5), recent: [during, before]));
        recorder.Enqueue(Frame(T0.AddSeconds(6), recent: [during, before], seq: 1));
        await recorder.FlushAsync();

        var data = store.LoadSessionData(session.Id)!;
        Assert.Equal("000000000002", Assert.Single(data.Requests).RequestId);
        Assert.Single(data.Events, e => e.Category == EventCategories.RuntimeRequestError);
    }

    [Fact]
    public async Task Connection_state_changes_and_gaps_become_events()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        await using var recorder = Recorder(store);
        var session = await recorder.StartAsync("run", SessionKind.Manual);

        recorder.Enqueue(Frame(T0));
        recorder.Enqueue(Frame(T0.AddSeconds(1), state: RuntimeConnectionState.Unreachable, seq: 1));
        recorder.Enqueue(Frame(T0.AddSeconds(2), state: RuntimeConnectionState.Unreachable, seq: 2));
        recorder.Enqueue(Frame(T0.AddSeconds(60), seq: 3));
        await recorder.FlushAsync();

        var events = store.LoadSessionData(session.Id)!.Events;
        Assert.Equal(3, events.Count(e => e.Category == EventCategories.RuntimeConnection));
        Assert.Single(events, e => e.Category == EventCategories.Gap);
        var runtime = store.LoadSessionData(session.Id)!.Runtime;
        Assert.Null(runtime[1].AnswerTokS); // no rates recorded while disconnected
    }
}
