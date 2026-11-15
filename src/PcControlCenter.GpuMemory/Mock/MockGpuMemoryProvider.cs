namespace PcControlCenter.GpuMemory.Mock;

/// <summary>
/// MOCK GPU memory for --mock-sensors runs, tests and the GUI smoke test: two adapters (a discrete NVIDIA card and an
/// integrated GPU), a local AI server whose engine child process holds most of the VRAM, a process that comes and goes and
/// whose PID is then reused by another process. Every reading is marked MOCK; nothing here is a measurement.
/// </summary>
public sealed class MockGpuMemoryProvider(TimeProvider? time = null) : IGpuMemoryProvider
{
    public const long NvidiaLuid = 0x0000_C8D1;
    public const long IntegratedLuid = 0x0000_D2A0;
    public const int ServerPort = 18500;
    public const int ServerPid = 7340;
    public const int EnginePid = 7412;
    public const int CyclingPid = 6000;
    public const int OllamaServerPid = 8100;
    public const int OllamaRunnerPid = 8120;
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly DateTimeOffset _bootTime = (time ?? TimeProvider.System).GetUtcNow() - TimeSpan.FromHours(3);

    public string Name => "MOCK (가상 GPU 메모리)";
    public bool IsMock => true;
    public string? UnavailableReason => null;

    public static IReadOnlyList<GpuAdapterInfo> AdapterInfos { get; } =
    [
        new(NvidiaLuid, "MOCK NVIDIA GeForce RTX 5080", GpuVendors.Nvidia, 16 * GiB - 300 * MiB, 32 * GiB - 400 * MiB, false),
        new(IntegratedLuid, "MOCK Intel(R) UHD Graphics 770", GpuVendors.Intel, 128 * MiB, 32 * GiB - 400 * MiB, false),
    ];

    /// <summary>Listening sockets of the simulated PC (only those on watched ports are reported, like the Windows provider).</summary>
    public static IReadOnlyList<TcpListenerEntry> Listeners { get; } =
    [
        new(ServerPort, ServerPid, IsIpv6: false, IsLoopbackOnly: true),
        new(47831, 7000, IsIpv6: false, IsLoopbackOnly: true),
    ];

    public GpuMemorySnapshot Read(GpuMemoryReadRequest request)
    {
        var now = _time.GetUtcNow();
        var t = (now - _bootTime).TotalSeconds;
        var wave = Math.Sin(t / 20.0);
        var engineLocal = (long)(9.4 * GiB + wave * 160 * MiB);
        var engineShared = (long)(820 * MiB + wave * 40 * MiB);
        var processes = new List<GpuProcessMemory>
        {
            new() { ProcessId = 4, Luid = NvidiaLuid, DedicatedBytes = 12 * MiB, SharedBytes = 0, CommittedBytes = 12 * MiB },
            new() { ProcessId = 1520, Luid = NvidiaLuid, DedicatedBytes = 910 * MiB, SharedBytes = 64 * MiB, CommittedBytes = 1010 * MiB },
            new() { ProcessId = 1520, Luid = IntegratedLuid, DedicatedBytes = 22 * MiB, SharedBytes = 48 * MiB, CommittedBytes = 70 * MiB },
            new() { ProcessId = EnginePid, Luid = NvidiaLuid, DedicatedBytes = engineLocal, SharedBytes = engineShared, CommittedBytes = engineLocal + engineShared + 380 * MiB },
            new() { ProcessId = ServerPid, Luid = NvidiaLuid, DedicatedBytes = 0, SharedBytes = 0, CommittedBytes = 0 },
            new() { ProcessId = 9120, Luid = NvidiaLuid, DedicatedBytes = 452 * MiB, SharedBytes = 31 * MiB, CommittedBytes = 500 * MiB },
            new() { ProcessId = 8800, Luid = NvidiaLuid, DedicatedBytes = 84 * MiB, SharedBytes = 2 * MiB, CommittedBytes = 90 * MiB },
            new() { ProcessId = 3100, Luid = NvidiaLuid, DedicatedBytes = 300 * 1024, SharedBytes = 0, CommittedBytes = 300 * 1024 },
            new() { ProcessId = OllamaRunnerPid, Luid = NvidiaLuid, DedicatedBytes = 1536 * MiB, SharedBytes = 96 * MiB, CommittedBytes = 1700 * MiB },
        };
        var table = new Dictionary<int, ProcessEntry>
        {
            [4] = new(4, 0, "System", null, ProcessIdentityState.StartTimeUnavailable),
            [1520] = new(1520, 900, "dwm.exe", null, ProcessIdentityState.StartTimeUnavailable),
            [5000] = new(5000, 4800, "explorer.exe", _bootTime.AddMinutes(1), ProcessIdentityState.Verified),
            [7000] = new(7000, 5000, "Strata.exe", _bootTime.AddMinutes(10), ProcessIdentityState.Verified),
            [ServerPid] = new(ServerPid, 7000, "python.exe", _bootTime.AddMinutes(11), ProcessIdentityState.Verified),
            [EnginePid] = new(EnginePid, ServerPid, "strata.exe", _bootTime.AddMinutes(11).AddSeconds(2), ProcessIdentityState.Verified),
            [9120] = new(9120, 5000, "chrome.exe", _bootTime.AddMinutes(20), ProcessIdentityState.Verified),
            [8800] = new(8800, 5000, "PcControlCenter.exe", _bootTime.AddMinutes(30), ProcessIdentityState.Verified),
            [3100] = new(3100, 5000, "notepad.exe", _bootTime.AddMinutes(40), ProcessIdentityState.Verified),
            [OllamaServerPid] = new(OllamaServerPid, 5000, "ollama.exe", _bootTime.AddMinutes(5), ProcessIdentityState.Verified),
            [OllamaRunnerPid] = new(OllamaRunnerPid, OllamaServerPid, "ollama.exe", _bootTime.AddMinutes(50), ProcessIdentityState.Verified),
        };

        // A process that runs 40 s of every 120 s; each time it starts, PID 6000 belongs to a new process (new start time).
        var cycle = (long)Math.Floor(t / 120.0);
        if (t - cycle * 120.0 < 40.0)
        {
            var start = _bootTime.AddSeconds(cycle * 120.0);
            var name = cycle % 2 == 0 ? "game.exe" : "renderer.exe";
            processes.Add(new GpuProcessMemory { ProcessId = CyclingPid, Luid = NvidiaLuid, DedicatedBytes = 2 * GiB, SharedBytes = 120 * MiB, CommittedBytes = 2 * GiB + 200 * MiB });
            table[CyclingPid] = new ProcessEntry(CyclingPid, 5000, name, start, ProcessIdentityState.Verified);
        }

        // Engine activity and details only when asked (the Windows provider reads them only while the page is visible).
        var details = new Dictionary<int, ProcessDetail>();
        EngineUsage? nvidiaEngines = null, integratedEngines = null;
        if (request.IncludeDetails)
        {
            var compute = Math.Round(42 + 8 * Math.Sin(t / 7.0), 1);
            var engines = new Dictionary<int, EngineUsage>
            {
                [4] = new(0, 0, 0.4, 0, 0),
                [1520] = new(3.5, 0, 0, 0, 0),
                [EnginePid] = new(0, compute, 1.5, 0, 0),
                [ServerPid] = EngineUsage.Idle,
                [9120] = new(1.2, 0, 0, 2.5, 0),
                [8800] = new(0.8, 0, 0, 0, 0),
                [3100] = EngineUsage.Idle,
                [CyclingPid] = new(61, 0, 2, 0, 0),
                [OllamaRunnerPid] = new(0, 6, 0.5, 0, 0),
            };
            processes = [.. processes.Select(p => p.Luid == NvidiaLuid && engines.TryGetValue(p.ProcessId, out var e) ? p with { Engines = e } : p.Luid == IntegratedLuid ? p with { Engines = new(1, 0, 0, 0, 0) } : p)];
            nvidiaEngines = new EngineUsage(processes.Where(p => p.Luid == NvidiaLuid).Sum(p => p.Engines?.ThreeD ?? 0), compute, 3.5,
                processes.Where(p => p.Luid == NvidiaLuid).Sum(p => p.Engines?.Video ?? 0), 0);
            integratedEngines = new EngineUsage(1, 0, 0, 0, 0);
            details[EnginePid] = new ProcessDetail
            {
                Runtimes = ["ggml", "CUDA"],
                LoadedLibraries = 84,
                ScannedAt = now,
                Models = [new ModelFile("MOCK-Qwen3-14B-Q4_K_M.gguf", 9_001_000_000, "GGUF", new GgufSummary
                {
                    Name = "MOCK Qwen3 14B",
                    Architecture = "qwen3",
                    Kind = "LLM",
                    Parameters = 14_770_000_000,
                    Quantization = "Q4_K_M",
                    Layers = 40,
                    TrainedContext = 40_960,
                    TensorBytes = 8_990_000_000,
                    GpuWeightBytesAllLayers = 8L * GiB,
                    KvBytesPerTokenF16 = 40L * 8 * (128 + 128) * 2,
                })],
            };
            details[ServerPid] = new ProcessDetail { Runtimes = [], LoadedLibraries = 31, ScannedAt = now };
            details[OllamaRunnerPid] = new ProcessDetail
            {
                Runtimes = ["llama.cpp", "CUDA"],
                LoadedLibraries = 70,
                ScannedAt = now,
                Models = [new ModelFile("MOCK-gemma3-4b-Q4_K_M.gguf", 3_340_000_000, "GGUF", new GgufSummary
                {
                    Name = "MOCK Gemma 3 4B",
                    Architecture = "gemma3",
                    Kind = "LLM",
                    Parameters = 4_300_000_000,
                    Quantization = "Q4_K_M",
                    Layers = 34,
                    TrainedContext = 131_072,
                    SlidingWindow = 1024,
                    TensorBytes = 3_320_000_000,
                    GpuWeightBytesAllLayers = 2_600_000_000, // more than the runner holds: part of the model stays in RAM
                    KvBytesPerTokenF16 = 34L * 4 * (256 + 256) * 2,
                })],
            };
            details[9120] = new ProcessDetail { Runtimes = ["D3D11"], LoadedLibraries = 160, ScannedAt = now };
            details[8800] = new ProcessDetail { Runtimes = ["D3D11"], LoadedLibraries = 120, ScannedAt = now };
            details[1520] = new ProcessDetail { AccessDenied = true, ScannedAt = now };
            if (processes.Any(p => p.ProcessId == CyclingPid))
            {
                details[CyclingPid] = new ProcessDetail { Runtimes = ["D3D12", "D3D11"], LoadedLibraries = 95, ScannedAt = now };
            }
        }

        var nvidiaProcessSum = processes.Where(p => p.Luid == NvidiaLuid).Sum(p => p.DedicatedBytes ?? 0);
        return new GpuMemorySnapshot
        {
            Timestamp = now,
            Source = "MOCK 가상 GPU 메모리 (측정 아님)",
            IsMock = true,
            Variant = ProcessCounterVariant.LocalNonLocal,
            CommittedAvailable = true,
            AdapterCountersAvailable = true,
            Adapters =
            [
                new GpuAdapterMemory { Luid = NvidiaLuid, Info = AdapterInfos[0], DedicatedUsageBytes = nvidiaProcessSum + 260 * MiB, SharedUsageBytes = engineShared + 230 * MiB, Engines = nvidiaEngines },
                new GpuAdapterMemory { Luid = IntegratedLuid, Info = AdapterInfos[1], DedicatedUsageBytes = 30 * MiB, SharedUsageBytes = 160 * MiB, Engines = integratedEngines },
            ],
            Processes = processes,
            ProcessTable = table,
            Listeners = Listeners.Where(l => request.WatchedPorts.Contains(l.Port)).ToList(),
            // The server listens on Ollama's port; its runner on one of its own, for the server only (the page asks the server).
            ServiceListeners = request.IncludeDetails
                ? [new TcpListenerEntry(MockRuntimeHttp.OllamaPort, OllamaServerPid, IsIpv6: false, IsLoopbackOnly: true),
                   new TcpListenerEntry(50123, OllamaRunnerPid, IsIpv6: false, IsLoopbackOnly: true)]
                : [],
            EnginesRead = request.IncludeDetails,
            Details = details,
            CollectionTime = TimeSpan.FromMilliseconds(1),
        };
    }

    public void Dispose()
    {
    }
}
