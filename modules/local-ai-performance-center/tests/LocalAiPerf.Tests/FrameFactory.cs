using LocalAiPerf.Core;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;

namespace LocalAiPerf.Tests;

internal static class FrameFactory
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static TelemetryFrame Frame(DateTimeOffset at, double gpuUtil = 80, double? tokS = 40, double power = 250, long seq = 0,
        string model = "model-a", string engine = "0.1.55", RuntimeConnectionState state = RuntimeConnectionState.Connected,
        IReadOnlyList<CompletedRequest>? recent = null, double? temp = 65, ulong throttle = 0, long? energyMj = null)
    {
        var hardware = new HardwareSnapshot
        {
            Timestamp = at,
            System = new SystemSample { CpuUtilizationPercent = 12.5, MemoryUsedBytes = 20L << 30, MemoryTotalBytes = 64L << 30 },
            Gpus =
            [
                new GpuSample
                {
                    Index = 0, Name = "Test GPU", UtilizationPercent = gpuUtil, MemoryControllerUtilizationPercent = 30, TemperatureC = temp,
                    PowerW = power, PowerLimitW = 360, MemoryUsedBytes = 12L << 30, MemoryTotalBytes = 16L << 30, GraphicsClockMhz = 2600,
                    MemoryClockMhz = 15000, FanSpeedPercent = null, PcieTxKBps = 1000, PcieRxKBps = 2000,
                    TotalEnergyMilliJoules = energyMj, ThrottleReasons = throttle,
                },
            ],
        };
        var reachable = state is RuntimeConnectionState.Connected or RuntimeConnectionState.Limited;
        var runtime = new RuntimeSnapshot
        {
            RuntimeKind = "strata",
            Timestamp = at,
            State = state,
            Error = reachable ? null : new RuntimeError(RuntimeErrorCategory.Connection, "down"),
            Server = reachable ? new RuntimeServerInfo { Service = "strata", EngineVersion = engine, InstanceId = "inst-1" } : null,
            Model = reachable ? new RuntimeModelInfo { Id = model, Loaded = true, ContextTokens = 32768 } : null,
            Work = reachable ? new RuntimeWorkInfo { Active = tokS is null ? 0 : 1, Queued = 0, SlotsEffective = 1 } : null,
            Rates = reachable ? new RuntimeRates { AnswerTokPerSec = tokS, PrefillTokPerSec = tokS is null ? null : 900, AggregateOutputTokPerSec = tokS, OutputTokensCounter = seq * 40 } : null,
            Slots = reachable ? [new RuntimeSlot(0, "decoding", 5000, 4000, 4000, 100, tokS)] : [],
            RecentRequests = recent ?? [],
        };
        return new TelemetryFrame(seq, at, hardware, runtime, TimeSpan.FromMilliseconds(5), 0);
    }

    public static SessionStartRequest Start(DateTimeOffset at, string name = "test", SessionKind kind = SessionKind.Manual)
        => new(name, kind, at, 1000, "0.1.0-test", "{\"cpuName\":\"Test CPU\"}", "strata", "http://127.0.0.1:8080", IsMock: false);
}
