using System.Diagnostics;
using Iron.ProcessIdentity;
using Iron.Stage2.HwInfo;
using Iron.Stage2.Windows;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Telemetry;
using PcControlCenter.GpuMemory;
using PcControlCenter.GpuMemory.Windows;

namespace Iron.Stage2;

public sealed record Stage2Options
{
    /// <summary>NVML GPUs, CPU load and RAM (the existing reader of this repository).</summary>
    public TimeSpan HardwareInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-process GPU memory (WDDM counters) and the process list given to the identity resolver.</summary>
    public TimeSpan ProcessInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>HWiNFO REST (loopback only); null = not used (CPU temperature, NVMe temperature and WireView stay N/A).</summary>
    public Uri? HwInfoUrl { get; init; } = new("http://127.0.0.1:8764/");

    public TimeSpan HwInfoInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Engine use per process (3D/Compute/Video) — read by the WDDM provider together with its process details.</summary>
    public bool GpuEngineDetails { get; init; } = true;
}

public sealed record Stage2Stats(long HardwareReads, long HardwareSkipped, double HardwareLastMs, long ProcessReads, long ProcessSkipped, double ProcessLastMs,
    HwInfoStats? HwInfo, ResolverStats? Identity);

/// <summary>
/// Owns the real readers on their own background threads and composes the newest values on demand (the UI timer calls
/// <see cref="Latest"/>, which never waits for a sensor). Each loop keeps its period and skips a period it missed instead
/// of catching up. Readers that cannot run on this PC report why and leave their values N/A.
/// </summary>
public sealed class Stage2Collector : IDisposable
{
    private readonly Stage2Options _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = [];
    private readonly HwInfoRestSource? _hwinfo;
    private volatile HardwareSnapshot? _hardware;
    private volatile GpuMemorySnapshot? _gpuMemory;
    private double? _diskRead;
    private volatile bool _gpuAvailable;
    private volatile SourceStatus _nvml = new("NVML (GPU)", SourceState.Connecting);
    private volatile SourceStatus _system = new("Windows CPU·RAM", SourceState.Connecting);
    private volatile SourceStatus _wddm = new("WDDM 프로세스 GPU 메모리", SourceState.Connecting);
    private volatile SourceStatus _disk = new("디스크 읽기(PDH)", SourceState.Connecting);
    private volatile SourceStatus _identity = new("프로세스 식별", SourceState.Connecting);
    private long _hwReads;
    private long _hwSkipped;
    private double _hwLastMs;
    private long _procReads;
    private long _procSkipped;
    private double _procLastMs;

    public Stage2Collector(Stage2Options? options = null, TimeProvider? time = null)
    {
        _options = options ?? new Stage2Options();
        _time = time ?? TimeProvider.System;
        Resolver = IdentityResolverFactory.TryCreateNative();
        if (Resolver is null)
        {
            _identity = new SourceStatus("프로세스 식별", SourceState.Unavailable, "Windows 전용");
        }

        if (_options.HwInfoUrl is { } url)
        {
            try
            {
                _hwinfo = new HwInfoRestSource(url, _options.HwInfoInterval, time: _time);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or System.Text.Json.JsonException)
            {
                HwInfoSetupError = ex.Message;
            }
        }
    }

    public ProcessIdentityResolver? Resolver { get; }

    public string? HwInfoSetupError { get; }

    public void Start()
    {
        _hwinfo?.Start();
        StartLoop("IRON hardware", ThreadPriority.Normal, HardwareLoop);
        StartLoop("IRON processes", ThreadPriority.BelowNormal, ProcessLoop);
    }

    private void StartLoop(string name, ThreadPriority priority, Action<CancellationToken> body)
    {
        var t = new Thread(() => body(_stop.Token)) { IsBackground = true, Name = name, Priority = priority };
        _threads.Add(t);
        t.Start();
    }

    public IReadOnlyList<SourceStatus> Sources => [_nvml, _system, _wddm, _disk, _identity, HwInfoStatus];

    private SourceStatus HwInfoStatus => _hwinfo?.Status ?? new SourceStatus("HWiNFO REST", SourceState.Off, HwInfoSetupError ?? "사용 안 함");

    public Stage2Stats Stats => new(Interlocked.Read(ref _hwReads), Interlocked.Read(ref _hwSkipped), _hwLastMs, Interlocked.Read(ref _procReads),
        Interlocked.Read(ref _procSkipped), _procLastMs, _hwinfo?.Stats, Resolver?.Stats);

    /// <summary>The newest values of every source as one frame (cheap; called by the UI timer).</summary>
    public Stage2Frame Latest(DateTimeOffset now) => Composer.Compose(now, _hardware, _gpuAvailable, _gpuMemory, _hwinfo?.Latest, _diskRead,
        pid => Resolver is not null && Resolver.TryGetByPid(pid, out var info) ? info : null, Sources);

    /// <summary>Waits on the period; returns false when stopping. Missed periods are skipped (counted), never caught up.</summary>
    private static bool WaitNext(Stopwatch clock, ref TimeSpan due, TimeSpan period, ref long skipped, CancellationToken ct)
    {
        due += period;
        var now = clock.Elapsed;
        if (now - due >= period)
        {
            var missed = (long)((now - due) / period);
            Interlocked.Add(ref skipped, missed);
            due += period * missed;
        }

        return due <= now || !ct.WaitHandle.WaitOne(due - now);
    }

    private void HardwareLoop(CancellationToken ct)
    {
        HardwareMonitor? monitor = null;
        DiskReadCounter? disk = null;
        try
        {
            monitor = HardwareMonitor.Create(new SamplingSettings { IntervalMs = (int)_options.HardwareInterval.TotalMilliseconds }, useMock: false, _time);
            _gpuAvailable = monitor.Gpu.IsAvailable;
            _nvml = new SourceStatus("NVML (GPU)", monitor.Gpu.IsAvailable ? SourceState.Live : SourceState.Unavailable, monitor.Gpu.UnavailableReason);
            if (OperatingSystem.IsWindows())
            {
                disk = DiskReadCounter.TryOpen(out var diskError);
                _disk = new SourceStatus("디스크 읽기(PDH)", disk is null ? SourceState.Unavailable : SourceState.Live, diskError);
            }
            else
            {
                _disk = new SourceStatus("디스크 읽기(PDH)", SourceState.Unavailable, "Windows 전용");
            }

            var clock = Stopwatch.StartNew();
            var due = TimeSpan.Zero - _options.HardwareInterval;
            while (WaitNext(clock, ref due, _options.HardwareInterval, ref _hwSkipped, ct) && !ct.IsCancellationRequested)
            {
                var t0 = clock.Elapsed;
                var snap = monitor.Sample();
                _hardware = snap;
                _system = new SourceStatus("Windows CPU·RAM", snap.System.CpuUtilizationPercent is null && snap.System.MemoryTotalBytes is null ? SourceState.Unavailable : SourceState.Live,
                    snap.Issues.FirstOrDefault(i => i.Scope == "system")?.Message);
                if (OperatingSystem.IsWindows())
                {
                    _diskRead = disk?.Read();
                }

                _hwLastMs = (clock.Elapsed - t0).TotalMilliseconds;
                Interlocked.Increment(ref _hwReads);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _nvml = new SourceStatus("NVML (GPU)", SourceState.Unavailable, "읽기 루프 오류: " + ex.Message);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                disk?.Dispose();
            }

            monitor?.Dispose();
        }
    }

    private void ProcessLoop(CancellationToken ct)
    {
        IGpuMemoryProvider? provider = null;
        try
        {
            provider = OperatingSystem.IsWindows() ? new WindowsGpuMemoryProvider(_time) : new UnavailableGpuMemoryProvider("Windows 전용", _time);
            _wddm = new SourceStatus("WDDM 프로세스 GPU 메모리", provider.UnavailableReason is null ? SourceState.Live : SourceState.Unavailable, provider.UnavailableReason);
            var request = new GpuMemoryReadRequest(new HashSet<int>(), _options.GpuEngineDetails);
            var clock = Stopwatch.StartNew();
            var due = TimeSpan.Zero - _options.ProcessInterval;
            while (WaitNext(clock, ref due, _options.ProcessInterval, ref _procSkipped, ct) && !ct.IsCancellationRequested)
            {
                var t0 = clock.Elapsed;
                if (Resolver is not null)
                {
                    // identities for every process (cheap list, no process opened here); resolved on the resolver's own workers
                    Resolver.Observe(Resolver.TakeSnapshot());
                    _identity = new SourceStatus("프로세스 식별", SourceState.Live, $"{Resolver.Stats.ProcessCache}개 식별");
                }

                if (provider.UnavailableReason is null)
                {
                    _gpuMemory = provider.Read(request);
                }

                _procLastMs = (clock.Elapsed - t0).TotalMilliseconds;
                Interlocked.Increment(ref _procReads);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _wddm = new SourceStatus("WDDM 프로세스 GPU 메모리", SourceState.Unavailable, "읽기 루프 오류: " + ex.Message);
        }
        finally
        {
            provider?.Dispose();
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        foreach (var t in _threads)
        {
            t.Join(TimeSpan.FromSeconds(3));
        }

        _hwinfo?.Dispose();
        Resolver?.Dispose();
        _stop.Dispose();
    }
}
