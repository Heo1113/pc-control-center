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
    HwInfoStats? HwInfo, ResolverStats? Identity, long HardwareErrors = 0, long ProcessErrors = 0);

/// <summary>A reading and when it was taken (monotonic <see cref="TimeProvider.GetTimestamp"/>), so its age can be judged.</summary>
internal sealed record Timed<T>(T Value, long At);

/// <summary>One pass of the hardware loop: NVML GPUs, CPU load and RAM, the disk read rate, and whether NVML answered.</summary>
internal sealed record HardwareReading(HardwareSnapshot Snapshot, double? DiskReadBytesPerSec, bool GpuAvailable)
{
    /// <summary>No values (a failed pass, or a loop that has stopped): GPU cards, CPU and RAM show N/A, never an older value.</summary>
    public static HardwareReading Empty(DateTimeOffset at) => new(new HardwareSnapshot { Timestamp = at }, null, false);
}

/// <summary>
/// How old a reading may be before it is no longer shown: three periods of its loop, at least 3 s (the HWiNFO rule of RC3.2).
/// A loop that hangs (a driver call that does not return) or has stopped thus never leaves its last values on screen as if
/// they were current; the source is reported as <see cref="SourceState.Stale"/> instead.
/// </summary>
internal static class Freshness
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(3);

    public static TimeSpan MaxAge(TimeSpan interval) => interval * 3 > MinAge ? interval * 3 : MinAge;

    public static SourceStatus Stale(SourceStatus s, TimeSpan age) =>
        s with { State = SourceState.Stale, Detail = $"마지막 값 {Math.Floor(age.TotalSeconds):0}초 전 — 표시하지 않음" };
}

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
    private volatile Timed<HardwareReading>? _hardware;
    private volatile Timed<GpuMemorySnapshot>? _gpuMemory;
    private volatile bool _nvmlEverLive;
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
    private long _hwErrors;
    private long _procErrors;

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

    /// <summary>State of every source; a source whose last reading is too old is reported as stale.</summary>
    public IReadOnlyList<SourceStatus> Sources => Snapshot().Sources;

    private SourceStatus HwInfoStatus => _hwinfo?.Status ?? new SourceStatus("HWiNFO REST", SourceState.Off, HwInfoSetupError ?? "사용 안 함");

    public Stage2Stats Stats => new(Interlocked.Read(ref _hwReads), Interlocked.Read(ref _hwSkipped), _hwLastMs, Interlocked.Read(ref _procReads),
        Interlocked.Read(ref _procSkipped), _procLastMs, _hwinfo?.Stats, Resolver?.Stats, Interlocked.Read(ref _hwErrors), Interlocked.Read(ref _procErrors));

    /// <summary>The newest values of every source as one frame (cheap; called by the UI timer).</summary>
    public Stage2Frame Latest(DateTimeOffset now)
    {
        var f = Snapshot();
        return Composer.Compose(now, f.Hardware?.Snapshot, f.Hardware?.GpuAvailable ?? false, f.GpuMemory, _hwinfo?.Latest, f.Hardware?.DiskReadBytesPerSec,
            pid => Resolver is not null && Resolver.TryGetByPid(pid, out var info) ? info : null, f.Sources, f.ProcessesUnavailable);
    }

    private FreshValues Snapshot() => Fresh(_time, _hardware, _options.HardwareInterval, _gpuMemory, _options.ProcessInterval,
        [_nvml, _system, _wddm, _disk, _identity, HwInfoStatus]);

    /// <param name="Hardware">Null until the first pass; an empty reading when the last one is too old.</param>
    /// <param name="GpuMemory">Null until the first read, after a failed read and when the last one is too old.</param>
    /// <param name="ProcessesUnavailable">Why there is no process list to show (shown instead of "no process uses the GPU").</param>
    internal sealed record FreshValues(HardwareReading? Hardware, GpuMemorySnapshot? GpuMemory, IReadOnlyList<SourceStatus> Sources, string? ProcessesUnavailable);

    /// <summary>
    /// Keeps only readings young enough to be current (pure; tested with a hand-set clock). Sources order: NVML, Windows
    /// CPU·RAM, WDDM, disk, identity, HWiNFO.
    /// </summary>
    internal static FreshValues Fresh(TimeProvider time, Timed<HardwareReading>? hardware, TimeSpan hardwareInterval, Timed<GpuMemorySnapshot>? gpuMemory,
        TimeSpan processInterval, SourceStatus[] sources)
    {
        HardwareReading? hw = null;
        if (hardware is not null)
        {
            var age = time.GetElapsedTime(hardware.At);
            if (age <= Freshness.MaxAge(hardwareInterval))
            {
                hw = hardware.Value;
            }
            else
            {
                hw = HardwareReading.Empty(time.GetUtcNow());
                sources[0] = StaleIfLive(sources[0], age); // NVML
                sources[1] = StaleIfLive(sources[1], age); // Windows CPU·RAM
                sources[3] = StaleIfLive(sources[3], age); // disk (read in the same loop)
            }
        }

        GpuMemorySnapshot? gm = null;
        string? unavailable = null;
        if (gpuMemory is not null)
        {
            var age = time.GetElapsedTime(gpuMemory.At);
            if (age <= Freshness.MaxAge(processInterval))
            {
                gm = gpuMemory.Value;
            }
            else
            {
                sources[2] = StaleIfLive(sources[2], age);
                unavailable = $"WDDM 카운터 값이 {Math.Floor(age.TotalSeconds):0}초째 들어오지 않아 프로세스 목록을 표시하지 않습니다.";
            }
        }
        else
        {
            unavailable = sources[2].State switch
            {
                SourceState.Connecting => "WDDM 카운터를 여는 중...",
                SourceState.Live => null,
                _ => "WDDM 카운터를 읽지 못했습니다" + (sources[2].Detail is { Length: > 0 } d ? ": " + d : "."),
            };
        }

        return new FreshValues(hw, gm, sources, unavailable);
    }

    private static SourceStatus StaleIfLive(SourceStatus s, TimeSpan age) => s.State is SourceState.Live or SourceState.Connecting ? Freshness.Stale(s, age) : s;

    /// <summary>NVML state of one pass: live while it reports GPUs; a lost GPU is "offline", never "live" with old values.</summary>
    internal static SourceStatus NvmlStatus(HardwareSnapshot snap, bool available, string? unavailableReason, bool everLive)
    {
        if (snap.Gpus.Count > 0)
        {
            return new SourceStatus("NVML (GPU)", SourceState.Live);
        }

        var issue = snap.Issues.FirstOrDefault(i => i.Scope == "gpu")?.Message;
        return everLive
            ? new SourceStatus("NVML (GPU)", SourceState.Offline, issue ?? "GPU가 응답하지 않습니다.")
            : new SourceStatus("NVML (GPU)", SourceState.Unavailable, available ? issue ?? "NVIDIA GPU가 없습니다." : unavailableReason ?? issue);
    }

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
                try
                {
                    var snap = monitor.Sample();
                    var available = monitor.Gpu.IsAvailable;
                    double? diskRead = null;
                    if (OperatingSystem.IsWindows())
                    {
                        diskRead = disk?.Read();
                    }

                    _hardware = new Timed<HardwareReading>(new HardwareReading(snap, diskRead, available && snap.Gpus.Count > 0), _time.GetTimestamp());
                    _nvml = NvmlStatus(snap, available, monitor.Gpu.UnavailableReason, _nvmlEverLive);
                    _nvmlEverLive |= snap.Gpus.Count > 0;
                    _system = new SourceStatus("Windows CPU·RAM", snap.System.CpuUtilizationPercent is null && snap.System.MemoryTotalBytes is null ? SourceState.Unavailable : SourceState.Live,
                        snap.Issues.FirstOrDefault(i => i.Scope == "system")?.Message);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // one failed pass: no values at once (not the previous ones), and the loop goes on
                    _hardware = new Timed<HardwareReading>(HardwareReading.Empty(_time.GetUtcNow()), _time.GetTimestamp());
                    _nvml = new SourceStatus("NVML (GPU)", SourceState.Offline, "읽기 오류: " + ex.Message);
                    _system = new SourceStatus("Windows CPU·RAM", SourceState.Offline, "읽기 오류: " + ex.Message);
                    Interlocked.Increment(ref _hwErrors);
                }

                _hwLastMs = (clock.Elapsed - t0).TotalMilliseconds;
                Interlocked.Increment(ref _hwReads);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // the loop itself could not run: nothing it read before stays on screen
            _hardware = new Timed<HardwareReading>(HardwareReading.Empty(_time.GetUtcNow()), _time.GetTimestamp());
            _nvml = new SourceStatus("NVML (GPU)", SourceState.Unavailable, "읽기 루프 오류: " + ex.Message);
            _system = new SourceStatus("Windows CPU·RAM", SourceState.Unavailable, "읽기 루프 오류: " + ex.Message);
            _disk = new SourceStatus("디스크 읽기(PDH)", SourceState.Unavailable, "읽기 루프 오류: " + ex.Message);
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
                    try
                    {
                        _gpuMemory = new Timed<GpuMemorySnapshot>(provider.Read(request), _time.GetTimestamp());
                        _wddm = new SourceStatus("WDDM 프로세스 GPU 메모리", SourceState.Live);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // a failed read: the list empties at once (it does not keep the previous processes) and the loop goes on
                        _gpuMemory = null;
                        _wddm = new SourceStatus("WDDM 프로세스 GPU 메모리", SourceState.Offline, "읽기 오류: " + ex.Message);
                        Interlocked.Increment(ref _procErrors);
                    }
                }

                _procLastMs = (clock.Elapsed - t0).TotalMilliseconds;
                Interlocked.Increment(ref _procReads);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _gpuMemory = null;
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
