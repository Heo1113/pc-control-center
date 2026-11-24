namespace Iron.App.Presentation;

/// <summary>How a WireView pin is drawn.</summary>
public enum PinLevel
{
    /// <summary>No telemetry (USB released, not connected): neutral, no warning color left behind.</summary>
    Inactive,
    Normal,

    /// <summary>At least 72 % of the UI threshold (amber, approaching).</summary>
    Approaching,

    /// <summary>At or above the UI threshold (red).</summary>
    Over,
}

/// <summary>
/// The two UI warnings of IRON, kept fully apart (independent review): the GPU temperature warning (NVML, per GPU,
/// immediate) and the WireView pin current warning (connector, latched only after three consecutive readings above the
/// threshold — the device protocol has no CRC). Releasing the WireView USB port or losing WireView clears only the pin
/// warning; it never touches the GPU warning. Both thresholds are UI emphasis only, not the device's own protection.
/// </summary>
public sealed class AlertMonitor
{
    /// <summary>Consecutive readings above the threshold before the pin warning latches (1 Hz → 3 s).</summary>
    public const int PinLatchReadings = 3;

    /// <summary>A pin at this share of the threshold is drawn amber (P4 5.1 A at 7.0 A in the design's alert mockup).</summary>
    public const double ApproachingShare = 0.72;

    private readonly List<bool> _gpuHot = [];
    private int _pinOverReadings;

    public IReadOnlyList<bool> GpuHot => _gpuHot;
    public bool AnyGpuHot => _gpuHot.Contains(true);

    /// <summary>Latched pin warning (drives the LED text "핀 불균형 주의").</summary>
    public bool PinAlert { get; private set; }

    /// <summary>Consecutive readings above the threshold so far (0..3), shown while the warning is being confirmed.</summary>
    public int PinOverReadings => _pinOverReadings;

    public static bool IsHot(double? temperatureC, double thresholdC) => temperatureC is double t && t >= thresholdC;

    public static PinLevel LevelOf(double currentA, double thresholdA, bool active)
        => !active ? PinLevel.Inactive
            : currentA >= thresholdA ? PinLevel.Over
            : currentA >= thresholdA * ApproachingShare ? PinLevel.Approaching
            : PinLevel.Normal;

    /// <summary>GPU temperature warning: re-evaluated on every reading and whenever the threshold changes.</summary>
    public void EvaluateGpus(IReadOnlyList<GpuReading> gpus, double thresholdC)
    {
        _gpuHot.Clear();
        foreach (var gpu in gpus)
        {
            _gpuHot.Add(IsHot(gpu.TemperatureC, thresholdC));
        }
    }

    /// <summary>
    /// One WireView reading. <paramref name="pins"/> null = no telemetry (released, resuming, not connected): the pin
    /// warning and its count are cleared at once. <paramref name="counts"/> false = a re-evaluation of the same moment (a
    /// switch was flipped), which may clear the warning but never advances the 3-reading confirmation.
    /// </summary>
    public void AddPinReading(IReadOnlyList<double>? pins, double thresholdA, bool counts = true)
    {
        if (pins is null || pins.Count == 0)
        {
            ResetPins();
            return;
        }

        var over = pins.Any(a => a >= thresholdA);
        if (!over)
        {
            _pinOverReadings = 0;
            PinAlert = false;
            return;
        }

        if (counts)
        {
            _pinOverReadings = Math.Min(PinLatchReadings, _pinOverReadings + 1);
            if (_pinOverReadings >= PinLatchReadings)
            {
                PinAlert = true;
            }
        }
    }

    public void ResetPins()
    {
        _pinOverReadings = 0;
        PinAlert = false;
    }
}

public enum StatusKind
{
    Online,
    Alert,
    Disconnected,
    Loading,
}

public sealed record StatusInfo(StatusKind Kind, string Text);

public static class StatusRules
{
    public const string OnlineText = "ONLINE";
    public const string PinText = "핀 불균형 주의";
    public const string GpuText = "GPU 온도 주의";
    public const string BothText = "핀 불균형 · GPU 온도 주의";
    public const string DisconnectedText = "미연결";
    public const string LoadingText = "센서 초기화 중...";

    /// <summary>The LED: loading, then disconnected, then any warning, then online.</summary>
    public static StatusInfo For(SensorPhase phase, bool anyGpuHot, bool pinAlert) => phase switch
    {
        SensorPhase.Initializing => new(StatusKind.Loading, LoadingText),
        SensorPhase.NoDevice => new(StatusKind.Disconnected, DisconnectedText),
        _ when pinAlert && anyGpuHot => new(StatusKind.Alert, BothText),
        _ when pinAlert => new(StatusKind.Alert, PinText),
        _ when anyGpuHot => new(StatusKind.Alert, GpuText),
        _ => new(StatusKind.Online, OnlineText),
    };
}
