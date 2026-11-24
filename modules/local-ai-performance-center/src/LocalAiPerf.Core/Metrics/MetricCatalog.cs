namespace LocalAiPerf.Core.Metrics;

/// <summary>Where a value comes from. The UI must never present a derived value as a measurement.</summary>
public enum MetricOrigin
{
    /// <summary>Read by this app from a sensor or OS API (NVML, Windows kernel32).</summary>
    Measured,

    /// <summary>Reported by the AI runtime through its public API (for example Strata /v1/telemetry).</summary>
    Reported,

    /// <summary>Calculated by this app from measured or reported values.</summary>
    Derived,
}

public enum MetricGroup
{
    Gpu,
    System,
    Runtime,
    Request,
    Efficiency,
}

public sealed record MetricDefinition(
    string Key,
    string DisplayName,
    string Unit,
    MetricOrigin Origin,
    MetricGroup Group,
    string Source,
    bool? HigherIsBetter,
    string Format = "F1")
{
    public string OriginLabel => Origin switch
    {
        MetricOrigin.Measured => "실측",
        MetricOrigin.Reported => "엔진 보고",
        MetricOrigin.Derived => "계산값",
        _ => Origin.ToString(),
    };

    public string FormatValue(double? value)
        => value is double v && double.IsFinite(v) ? v.ToString(Format, System.Globalization.CultureInfo.InvariantCulture) : "N/A";
}

public static class MetricKeys
{
    public const string GpuUtil = "gpu.util";
    public const string GpuMemUtil = "gpu.mem_util";
    public const string GpuTemp = "gpu.temp";
    public const string GpuPower = "gpu.power";
    public const string GpuPowerLimit = "gpu.power_limit";
    public const string GpuVramUsedGiB = "gpu.vram_used_gib";
    public const string GpuVramUsedPct = "gpu.vram_used_pct";
    public const string GpuClockGraphics = "gpu.clock_gfx";
    public const string GpuClockMemory = "gpu.clock_mem";
    public const string GpuFan = "gpu.fan";
    public const string GpuPcieTx = "gpu.pcie_tx";
    public const string GpuPcieRx = "gpu.pcie_rx";
    public const string CpuUtil = "cpu.util";
    public const string RamUsedGiB = "ram.used_gib";
    public const string RamUsedPct = "ram.used_pct";
    public const string RtAnswerTokS = "rt.answer_tok_s";
    public const string RtPrefillTokS = "rt.prefill_tok_s";
    public const string RtAggregateTokS = "rt.aggregate_tok_s";
    public const string RtActive = "rt.active";
    public const string RtQueued = "rt.queued";
    public const string RtContextUsed = "rt.context_used";
    public const string RtContextFillPct = "rt.context_fill_pct";
    public const string ReqDecodeTokS = "req.decode_tok_s";
    public const string ReqPrefillTokS = "req.prefill_tok_s";
    public const string ReqFirstTokenS = "req.first_token_s";
    public const string ReqDurationS = "req.duration_s";
    public const string ReqOutputTokens = "req.output_tokens";
    public const string EffTokensPerJoule = "eff.tokens_per_joule";
}

/// <summary>The single list of metrics the app records, shows, exports and compares.</summary>
public static class MetricCatalog
{
    private const string Nvml = "NVIDIA NVML (드라이버 공식 API)";
    private const string WinApi = "Windows API (GetSystemTimes / GlobalMemoryStatusEx)";
    private const string StrataTelemetry = "Strata /v1/telemetry";
    private const string StrataRecent = "Strata 완료 요청 요약 (recent[])";

    public static IReadOnlyList<MetricDefinition> All { get; } =
    [
        new(MetricKeys.GpuUtil, "GPU 사용률", "%", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F0"),
        new(MetricKeys.GpuMemUtil, "GPU 메모리 컨트롤러 사용률", "%", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F0"),
        new(MetricKeys.GpuTemp, "GPU 온도", "°C", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, false, "F0"),
        new(MetricKeys.GpuPower, "GPU 전력", "W", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, false, "F1"),
        new(MetricKeys.GpuPowerLimit, "GPU 전력 제한", "W", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F0"),
        new(MetricKeys.GpuVramUsedGiB, "VRAM 사용량", "GiB", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, false, "F2"),
        new(MetricKeys.GpuVramUsedPct, "VRAM 사용 비율", "%", MetricOrigin.Derived, MetricGroup.Gpu, "VRAM 사용량 ÷ 전체 용량", false, "F1"),
        new(MetricKeys.GpuClockGraphics, "GPU 그래픽 클럭", "MHz", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F0"),
        new(MetricKeys.GpuClockMemory, "GPU 메모리 클럭", "MHz", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F0"),
        new(MetricKeys.GpuFan, "GPU 팬 속도", "%", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F0"),
        new(MetricKeys.GpuPcieTx, "PCIe 송신 (GPU→호스트)", "MB/s", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F1"),
        new(MetricKeys.GpuPcieRx, "PCIe 수신 (호스트→GPU)", "MB/s", MetricOrigin.Measured, MetricGroup.Gpu, Nvml, null, "F1"),
        new(MetricKeys.CpuUtil, "CPU 사용률", "%", MetricOrigin.Measured, MetricGroup.System, WinApi, null, "F1"),
        new(MetricKeys.RamUsedGiB, "시스템 RAM 사용량", "GiB", MetricOrigin.Measured, MetricGroup.System, WinApi, false, "F2"),
        new(MetricKeys.RamUsedPct, "시스템 RAM 사용 비율", "%", MetricOrigin.Derived, MetricGroup.System, "RAM 사용량 ÷ 전체 용량", false, "F1"),
        new(MetricKeys.RtAnswerTokS, "답변 생성 속도", "tok/s", MetricOrigin.Reported, MetricGroup.Runtime, StrataTelemetry + " rates.answer_tok_s", true, "F1"),
        new(MetricKeys.RtPrefillTokS, "프롬프트 처리 속도", "tok/s", MetricOrigin.Reported, MetricGroup.Runtime, StrataTelemetry + " rates.prefill_tok_s", true, "F1"),
        new(MetricKeys.RtAggregateTokS, "전체 출력 처리량", "tok/s", MetricOrigin.Derived, MetricGroup.Runtime, "output_tokens_server_counter 증가분 ÷ 폴링 간격", true, "F1"),
        new(MetricKeys.RtActive, "처리 중 요청", "건", MetricOrigin.Reported, MetricGroup.Runtime, StrataTelemetry + " work.active", null, "F0"),
        new(MetricKeys.RtQueued, "대기 요청", "건", MetricOrigin.Reported, MetricGroup.Runtime, StrataTelemetry + " work.queued", false, "F0"),
        new(MetricKeys.RtContextUsed, "최대 슬롯 컨텍스트 사용량", "tokens", MetricOrigin.Reported, MetricGroup.Runtime, StrataTelemetry + " work.slots[].held_tokens", null, "F0"),
        new(MetricKeys.RtContextFillPct, "컨텍스트 사용 비율", "%", MetricOrigin.Derived, MetricGroup.Runtime, "최대 슬롯 held_tokens ÷ model.context", null, "F1"),
        new(MetricKeys.ReqDecodeTokS, "요청별 디코드 속도", "tok/s", MetricOrigin.Derived, MetricGroup.Request, StrataRecent + ": output_tokens ÷ decode_ms", true, "F1"),
        new(MetricKeys.ReqPrefillTokS, "요청별 프롬프트 처리 속도", "tok/s", MetricOrigin.Derived, MetricGroup.Request, StrataRecent + ": (prompt_tokens − reused) ÷ prompt_ms", true, "F1"),
        new(MetricKeys.ReqFirstTokenS, "첫 토큰까지 (엔진 기준)", "s", MetricOrigin.Reported, MetricGroup.Request, StrataRecent + " first_engine_token_s (클라이언트 TTFT 아님)", false, "F2"),
        new(MetricKeys.ReqDurationS, "요청 처리 시간", "s", MetricOrigin.Reported, MetricGroup.Request, StrataRecent + " duration_s", false, "F2"),
        new(MetricKeys.ReqOutputTokens, "요청별 출력 토큰", "tokens", MetricOrigin.Reported, MetricGroup.Request, StrataRecent + " output_tokens", null, "F0"),
        new(MetricKeys.EffTokensPerJoule, "전력 효율 (토큰/J)", "tok/J", MetricOrigin.Derived, MetricGroup.Efficiency, "전체 출력 처리량 ÷ 같은 시점 GPU 전력", true, "F2"),
    ];

    private static readonly Dictionary<string, MetricDefinition> ByKey = All.ToDictionary(m => m.Key, StringComparer.Ordinal);

    public static MetricDefinition Get(string key)
        => ByKey.TryGetValue(key, out var definition) ? definition : throw new KeyNotFoundException($"Unknown metric '{key}'.");

    public static bool TryGet(string key, out MetricDefinition definition)
    {
        if (ByKey.TryGetValue(key, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }
}
