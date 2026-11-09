using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;

namespace LocalAiPerf.Core;

/// <summary>One collection tick: hardware readings plus the runtime poll taken at the same time.</summary>
public sealed record TelemetryFrame(
    long Sequence,
    DateTimeOffset Timestamp,
    HardwareSnapshot Hardware,
    RuntimeSnapshot? Runtime,
    TimeSpan CollectDuration,
    int SkippedTicks);
