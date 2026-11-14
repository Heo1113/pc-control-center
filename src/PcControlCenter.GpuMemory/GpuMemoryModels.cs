using LocalAiPerf.Core.Hardware;

namespace PcControlCenter.GpuMemory;

/// <summary>
/// Where a number comes from. Every value on the GPU memory page carries one of these; only <see cref="Measured"/>
/// values are ever added up, and an inference is never shown as a measurement.
/// </summary>
public enum ValueOrigin
{
    /// <summary>Reported by Windows (a performance counter or DXGI) for exactly this adapter or process.</summary>
    Measured,

    /// <summary>Arithmetic on measured values (a sum, a difference). Inherits their limits.</summary>
    Computed,

    /// <summary>Reported by a runtime's own API (Strata telemetry), not measured by this app.</summary>
    RuntimeReported,

    /// <summary>Derived from assumptions (model size, structure). Not used on this page; kept so the legend is complete.</summary>
    Estimated,
}

/// <summary>Which per-process counter pair Windows accepted. The preferred pair adds up much closer to the adapter total.</summary>
public enum ProcessCounterVariant
{
    /// <summary>Nothing could be read per process.</summary>
    Unavailable,

    /// <summary><c>Local Usage</c> / <c>Non Local Usage</c>: memory charged to the process, in local (VRAM) and non-local (system RAM) segments.</summary>
    LocalNonLocal,

    /// <summary>
    /// <c>Dedicated Usage</c> / <c>Shared Usage</c> (fallback): also counts surfaces shared from other processes, so the same
    /// bytes can appear under several processes (for example an app, csrss.exe and dwm.exe).
    /// </summary>
    DedicatedShared,
}

/// <summary>A GPU adapter as DXGI describes it. Capacities are what the driver reports to DXGI, not live usage.</summary>
public sealed record GpuAdapterInfo(
    long Luid,
    string Name,
    int VendorId,
    long DedicatedVideoMemoryBytes,
    long SharedSystemMemoryBytes,
    bool IsSoftware)
{
    public string LuidText => GpuLuid.Format(Luid);

    public string VendorName => GpuVendors.Name(VendorId);

    public bool IsNvidia => VendorId == GpuVendors.Nvidia;
}

public static class GpuVendors
{
    public const int Nvidia = 0x10DE;
    public const int Amd = 0x1002;
    public const int Intel = 0x8086;
    public const int Microsoft = 0x1414;

    public static string Name(int vendorId) => vendorId switch
    {
        Nvidia => "NVIDIA",
        Amd => "AMD",
        Intel => "Intel",
        Microsoft => "Microsoft",
        0x5143 or 0x4D4F4351 => "Qualcomm",
        _ => $"0x{vendorId:X4}",
    };
}

public static class GpuLuid
{
    /// <summary>The adapter LUID as the counters spell it: luid_0x{high}_0x{low}.</summary>
    public static string Format(long luid) => $"0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}";

    public static long From(uint high, uint low) => ((long)high << 32) | low;
}

/// <summary>Adapter-wide usage from <c>\GPU Adapter Memory(*)</c>. Null = not reported, never zero.</summary>
public sealed record GpuAdapterMemory
{
    public required long Luid { get; init; }

    /// <summary>DXGI's description of this LUID, or null when DXGI did not list it (the counters still identify it by LUID).</summary>
    public GpuAdapterInfo? Info { get; init; }

    /// <summary><c>Dedicated Usage</c>: bytes in the adapter's dedicated (local) memory.</summary>
    public long? DedicatedUsageBytes { get; init; }

    /// <summary><c>Shared Usage</c>: bytes of system RAM the adapter uses as GPU memory. Not the same as LLM offloading.</summary>
    public long? SharedUsageBytes { get; init; }

    /// <summary>
    /// Engine activity from <c>\GPU Engine(*)\Utilization Percentage</c>: per group, the busiest engine (its processes added).
    /// Null when engines were not read for this reading (they are read while the page is visible) or not reported.
    /// </summary>
    public EngineUsage? Engines { get; init; }
}

/// <summary>Engine groups as Task Manager shows them. CUDA work can appear on the 3D engine as well as on compute engines.</summary>
public enum EngineGroup
{
    ThreeD,
    Compute,
    Copy,
    Video,
    Other,
}

/// <summary>Busy percentages (0-100) per engine group. Utilization is the busiest group, as in Task Manager.</summary>
public sealed record EngineUsage(double ThreeD, double Compute, double Copy, double Video, double Other)
{
    public static EngineUsage Idle { get; } = new(0, 0, 0, 0, 0);

    public double Busiest => Math.Max(Math.Max(ThreeD, Compute), Math.Max(Math.Max(Copy, Video), Other));

    public double Of(EngineGroup group) => group switch
    {
        EngineGroup.ThreeD => ThreeD,
        EngineGroup.Compute => Compute,
        EngineGroup.Copy => Copy,
        EngineGroup.Video => Video,
        _ => Other,
    };
}

/// <summary>One process on one adapter from <c>\GPU Process Memory(*)</c>. Null = this counter was not reported, never zero.</summary>
public sealed record GpuProcessMemory
{
    public required int ProcessId { get; init; }
    public required long Luid { get; init; }

    /// <summary>Local segment (VRAM) bytes charged to the process: <c>Local Usage</c>, or <c>Dedicated Usage</c> in the fallback.</summary>
    public long? DedicatedBytes { get; init; }

    /// <summary>Non-local segment (system RAM) bytes: <c>Non Local Usage</c>, or <c>Shared Usage</c> in the fallback.</summary>
    public long? SharedBytes { get; init; }

    /// <summary><c>Total Committed</c>: all GPU memory the process allocated on the adapter, resident or not.</summary>
    public long? CommittedBytes { get; init; }

    /// <summary>The process's engine activity on this adapter; null when engines were not read or not reported.</summary>
    public EngineUsage? Engines { get; init; }
}

/// <summary>
/// What a process has loaded, from the list of files mapped into it (VirtualQueryEx + GetMappedFileName): libraries tell
/// the runtime (CUDA, ggml, ONNX Runtime ...), mapped data files the model. Only names and file headers on disk are read,
/// never the process's memory or command line.
/// </summary>
public sealed record ProcessDetail
{
    /// <summary>Runtimes recognised from loaded libraries, most specific first. Detection, not proof that each one does GPU work.</summary>
    public IReadOnlyList<string> Runtimes { get; init; } = [];

    /// <summary>Model files mapped into the process (file name only, never the full path).</summary>
    public IReadOnlyList<ModelFile> Models { get; init; } = [];

    /// <summary>The process refused the query rights (protected or another user's): runtimes and models are unknown.</summary>
    public bool AccessDenied { get; init; }

    /// <summary>The scan stopped at its time budget; lists can be incomplete.</summary>
    public bool Partial { get; init; }

    public int LoadedLibraries { get; init; }
    public DateTimeOffset ScannedAt { get; init; }

    /// <summary>The process that was scanned (PID and start time): a detail is shown only for that process.</summary>
    public ProcessKey? Key { get; init; }
}

/// <summary>A model file mapped into a process. A mapping proves the file is open in the process, not that it sits on the GPU.</summary>
public sealed record ModelFile(string FileName, long SizeBytes, string Format, GgufSummary? Gguf);

/// <summary>Facts from a GGUF header (read from the file on disk) and what follows from them by arithmetic.</summary>
public sealed record GgufSummary
{
    public string? Name { get; init; }
    public string? Architecture { get; init; }

    /// <summary>LLM, embedding, reranker, vision projector, LoRA adapter, speech or model (from header keys only).</summary>
    public required string Kind { get; init; }

    public long Parameters { get; init; }

    /// <summary>From general.file_type (a header fact, not guessed from the file name).</summary>
    public string? Quantization { get; init; }

    public int Layers { get; init; }
    public long? TrainedContext { get; init; }
    public int? SlidingWindow { get; init; }
    public int? Experts { get; init; }

    /// <summary>All tensor data in the file.</summary>
    public long TensorBytes { get; init; }

    /// <summary>Tensor bytes llama.cpp would place on the GPU with every layer offloaded (input embeddings stay on the CPU).</summary>
    public long GpuWeightBytesAllLayers { get; init; }

    /// <summary>KV cache bytes per token with f16 keys and values (all layers); null when the header lacks the dimensions.</summary>
    public long? KvBytesPerTokenF16 { get; init; }

    public bool IsCausal { get; init; } = true;

    /// <summary>
    /// Why KV per token cannot be computed from the header (<see cref="KvBytesPerTokenF16"/> is null then): recurrent or
    /// linear layers mixed with attention (SSM, RWKV, short convolutions, a full-attention interval), or latent attention
    /// (MLA, which caches a compressed vector instead of keys and values per head).
    /// </summary>
    public string? KvUnknownReason { get; init; }

    /// <summary>Why the header could not be read completely (the other fields are then partial or empty).</summary>
    public string? Error { get; init; }
}

/// <summary>How well the process behind a PID is known at the moment of the counter sample.</summary>
public enum ProcessIdentityState
{
    /// <summary>Name and start time read; the process started before the sample, so the counters are its own.</summary>
    Verified,

    /// <summary>Name known (process list), start time not readable (protected process or another user's): PID reuse cannot be ruled out.</summary>
    StartTimeUnavailable,

    /// <summary>The PID was gone from the process list right after the sample (the process ended).</summary>
    Exited,

    /// <summary>The process now behind this PID started after the sample: the counters belonged to an earlier process.</summary>
    ReusedAfterSample,
}

/// <summary>A process seen right after the counter sample. Name is the executable file name only (never a path or command line).</summary>
public sealed record ProcessEntry(int ProcessId, int ParentProcessId, string Name, DateTimeOffset? StartTime, ProcessIdentityState State)
{
    /// <summary>Identity across samples: a reused PID with another start time (or, without one, another name) is another process.</summary>
    public ProcessKey Key => new(ProcessId, StartTime?.UtcTicks ?? 0, StartTime is null ? Name : null);
}

/// <summary>Identifies one process across samples (PID plus start time, or PID plus name when the start time is unknown).</summary>
public readonly record struct ProcessKey(int ProcessId, long StartTicks, string? NameWhenNoStartTime);

/// <summary>A TCP socket in the listening state and the process that owns it (GetExtendedTcpTable).</summary>
public sealed record TcpListenerEntry(int Port, int ProcessId, bool IsIpv6, bool IsLoopbackOnly);

/// <summary>One reading of a <see cref="IGpuMemoryProvider"/>. Immutable; safe to hand to the UI thread.</summary>
public sealed record GpuMemorySnapshot
{
    /// <summary>When Windows collected the counters (right after PdhCollectQueryData returned).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Human-readable data source, for example "Windows 성능 카운터 (WDDM: GPU Process Memory · GPU Adapter Memory)".</summary>
    public required string Source { get; init; }

    public bool IsMock { get; init; }
    public ProcessCounterVariant Variant { get; init; }

    /// <summary>False when <c>Total Committed</c> could not be added: committed bytes are N/A for every process.</summary>
    public bool CommittedAvailable { get; init; }

    /// <summary>False when the adapter-wide counters could not be added: adapter usage is N/A.</summary>
    public bool AdapterCountersAvailable { get; init; }

    public IReadOnlyList<GpuAdapterMemory> Adapters { get; init; } = [];
    public IReadOnlyList<GpuProcessMemory> Processes { get; init; } = [];

    /// <summary>Processes with GPU memory, their ancestors (up to three levels) and the owners of TCP listeners, by PID.</summary>
    public IReadOnlyDictionary<int, ProcessEntry> ProcessTable { get; init; } = new Dictionary<int, ProcessEntry>();

    public IReadOnlyList<TcpListenerEntry> Listeners { get; init; } = [];

    /// <summary>Engine counters were read for this reading (they are read only while the page is visible).</summary>
    public bool EnginesRead { get; init; }

    /// <summary>Loaded libraries and mapped model files of the larger GPU processes, by PID (scanned within a time budget).</summary>
    public IReadOnlyDictionary<int, ProcessDetail> Details { get; init; } = new Dictionary<int, ProcessDetail>();

    /// <summary>Problems of this reading (a counter missing, a lookup failing). Values affected are null.</summary>
    public IReadOnlyList<SensorIssue> Issues { get; init; } = [];

    /// <summary>Wall time this reading took on the reader thread.</summary>
    public TimeSpan CollectionTime { get; init; }

    /// <summary>The reader's interval when it took this reading (set by the monitor; used to judge freshness).</summary>
    public TimeSpan ExpectedInterval { get; init; }

    public ProcessEntry? Process(int pid) => ProcessTable.TryGetValue(pid, out var entry) ? entry : null;

    public ProcessDetail? Detail(int pid) => Details.TryGetValue(pid, out var detail) ? detail : null;

    public GpuAdapterMemory? Adapter(long luid) => Adapters.FirstOrDefault(a => a.Luid == luid);
}
