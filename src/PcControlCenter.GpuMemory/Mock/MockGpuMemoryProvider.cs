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
                new GpuAdapterMemory { Luid = NvidiaLuid, Info = AdapterInfos[0], DedicatedUsageBytes = nvidiaProcessSum + 260 * MiB, SharedUsageBytes = engineShared + 230 * MiB },
                new GpuAdapterMemory { Luid = IntegratedLuid, Info = AdapterInfos[1], DedicatedUsageBytes = 30 * MiB, SharedUsageBytes = 160 * MiB },
            ],
            Processes = processes,
            ProcessTable = table,
            Listeners = Listeners.Where(l => request.WatchedPorts.Contains(l.Port)).ToList(),
            CollectionTime = TimeSpan.FromMilliseconds(1),
        };
    }

    public void Dispose()
    {
    }
}
