using System.Diagnostics;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>
/// GPU memory as Windows accounts it: WDDM performance counters per adapter and per process (PDH), adapter names from
/// DXGI, process names and start times, and the TCP listeners. Works as a normal user and never opens NVML: on GeForce
/// cards under WDDM, NVML reports per-process memory as not available (Windows' kernel manages that memory), while these
/// counters cover every API (Direct3D, Vulkan, OpenGL and CUDA).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGpuMemoryProvider : IGpuMemoryProvider
{
    private static readonly TimeSpan AdapterRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnknownLuidRefresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InspectionBudget = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DeviceMapRefresh = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly PdhGpuCounters? _counters;
    private IReadOnlyList<GpuAdapterInfo> _adapters;
    private string? _adapterError;
    private long _adaptersReadAt;
    private readonly ProcessInspector _inspector;
    private readonly BackgroundInspection _inspection;
    private DosDevices? _devices;

    public WindowsGpuMemoryProvider(TimeProvider? time = null, IAppLogger? log = null)
    {
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _counters = PdhGpuCounters.Open(_time, out var error);
        _adapters = DxgiAdapters.Enumerate(out _adapterError);
        _adaptersReadAt = Stopwatch.GetTimestamp();
        _inspector = new ProcessInspector((pid, budget) => ProcessScanner.Scan(pid, Devices(), budget), _time);
        // Scans, file lookups and model headers run beside the counters, never in front of them.
        _inspection = new BackgroundInspection((processes, table) => _inspector.Inspect(processes, table, InspectionBudget), _time);
        if (_counters is null)
        {
            UnavailableReason = error;
        }
        else if (!_counters.AnyAvailable)
        {
            UnavailableReason = "Windows GPU 성능 카운터(GPU Process Memory, GPU Adapter Memory)를 이 PC에서 쓸 수 없습니다. "
                                + string.Join(" ", _counters.SetupIssues.Select(i => i.Message));
            _counters.Dispose();
            _counters = null;
        }

        _log.Info("gpu-memory", $"Windows GPU 메모리 카운터: {(_counters is null ? "사용 불가" : _counters.Variant.ToString())}, DXGI 어댑터 {_adapters.Count}개"
                                + (_adapterError is null ? "" : $" ({_adapterError})"));
    }

    public string Name => "Windows 성능 카운터 (WDDM)";
    public bool IsMock => false;
    public string? UnavailableReason { get; }

    public GpuMemorySnapshot Read(GpuMemoryReadRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var issues = new List<SensorIssue>();
        if (_counters is null)
        {
            return new GpuMemorySnapshot
            {
                Timestamp = _time.GetUtcNow(),
                Source = Name,
                Issues = [new SensorIssue("Windows PDH", "counters", SensorStatus.Unavailable, UnavailableReason ?? "사용 불가")],
                CollectionTime = Stopwatch.GetElapsedTime(started),
            };
        }

        issues.AddRange(_counters.SetupIssues);
        var collected = _counters.Collect(issues);
        var (processes, adapterUsage) = CounterInstances.Merge(collected.ProcessDedicated, collected.ProcessShared, collected.ProcessCommitted,
            collected.AdapterDedicated, collected.AdapterShared, out var unrecognized);
        if (unrecognized > 0)
        {
            issues.Add(new SensorIssue("Windows PDH", "instances", SensorStatus.Error,
                $"형식을 알 수 없는 카운터 인스턴스 {unrecognized}개를 제외했습니다 (Windows 버전에 따라 이름 형식이 다를 수 있음)."));
        }

        // Engine activity and process details cost more than the memory counters: only while the page is visible.
        IReadOnlyDictionary<long, EngineUsage> adapterEngines = new Dictionary<long, EngineUsage>();
        var enginesRead = false;
        if (request.IncludeDetails && _counters.EnginesAvailable)
        {
            var (items, _) = _counters.CollectEngines(issues);
            if (items is not null)
            {
                var (perProcess, perAdapter) = CounterInstances.MergeEngines(items, out var unknownEngines);
                processes = [.. processes.Select(p => perProcess.TryGetValue((p.ProcessId, p.Luid), out var e) ? p with { Engines = e } : p)];
                adapterEngines = perAdapter;
                enginesRead = true;
                if (unknownEngines > 0)
                {
                    issues.Add(new SensorIssue("Windows PDH", "engine.instances", SensorStatus.Error, $"형식을 알 수 없는 엔진 인스턴스 {unknownEngines}개를 제외했습니다."));
                }
            }
        }
        else
        {
            _counters.PauseEngines();
        }

        RefreshAdaptersIfNeeded(processes.Select(p => p.Luid).Concat(adapterUsage.Keys));
        if (_adapterError is not null)
        {
            issues.Add(new SensorIssue("DXGI", "adapters", SensorStatus.Error, _adapterError));
        }

        // Hardware adapters always; software adapters (Microsoft Basic Render Driver) only when the counters report them.
        var luids = _adapters.Where(a => !a.IsSoftware).Select(a => a.Luid)
            .Concat(adapterUsage.Keys)
            .Concat(processes.Select(p => p.Luid))
            .Distinct()
            .ToList();
        var adapters = luids
            .Select(luid =>
            {
                var usage = adapterUsage.TryGetValue(luid, out var u) ? u : (Dedicated: (long?)null, Shared: (long?)null);
                return new GpuAdapterMemory
                {
                    Luid = luid,
                    Info = _adapters.FirstOrDefault(a => a.Luid == luid),
                    DedicatedUsageBytes = usage.Dedicated,
                    SharedUsageBytes = usage.Shared,
                    Engines = enginesRead ? adapterEngines.GetValueOrDefault(luid, EngineUsage.Idle) : null,
                };
            })
            .OrderBy(a => a.Info is null ? 2 : a.Info.IsSoftware ? 1 : 0)
            .ThenByDescending(a => a.Info?.DedicatedVideoMemoryBytes ?? 0)
            .ThenBy(a => a.Luid)
            .ToList();

        // Only the listeners of watched ports (the Strata server's): other services are never looked up.
        List<TcpListenerEntry>? all = null;
        List<TcpListenerEntry> AllListeners() => all ??= ProcessQueries.Listeners(issues);
        var listeners = request.WatchedPorts.Count == 0 ? [] : AllListeners().Where(l => request.WatchedPorts.Contains(l.Port)).ToList();
        var table = ProcessQueries.Table(processes.Select(p => p.ProcessId).Concat(listeners.Select(l => l.ProcessId)), collected.CollectedAt, issues);
        var details = request.IncludeDetails ? _inspection.Next(processes, table, issues) : new Dictionary<int, ProcessDetail>();
        // Local AI servers among these processes and their parents (an Ollama server holds no GPU memory itself; its runner does).
        var servers = request.IncludeDetails ? table.Values.Where(e => LocalAiServers.IsServerName(e.Name)).Select(e => e.ProcessId).ToHashSet() : [];
        var serviceListeners = servers.Count == 0 ? [] : AllListeners().Where(l => servers.Contains(l.ProcessId)).ToList();
        return new GpuMemorySnapshot
        {
            Timestamp = collected.CollectedAt,
            Source = "Windows 성능 카운터 (WDDM: GPU Process Memory · GPU Adapter Memory), DXGI 어댑터 정보",
            Variant = _counters.Variant,
            CommittedAvailable = _counters.CommittedAvailable,
            AdapterCountersAvailable = _counters.AdapterCountersAvailable,
            Adapters = adapters,
            Processes = processes,
            ProcessTable = table,
            Listeners = listeners,
            ServiceListeners = serviceListeners,
            EnginesRead = enginesRead,
            Details = details,
            Issues = issues,
            CollectionTime = Stopwatch.GetElapsedTime(started),
        };
    }

    public void Dispose() => _counters?.Dispose();

    private DosDevices Devices()
    {
        if (_devices is null || DateTimeOffset.UtcNow - _devices.LoadedAt > DeviceMapRefresh)
        {
            _devices = DosDevices.Load();
        }

        return _devices;
    }

    private void RefreshAdaptersIfNeeded(IEnumerable<long> seenLuids)
    {
        var age = Stopwatch.GetElapsedTime(_adaptersReadAt);
        var unknown = seenLuids.Any(luid => _adapters.All(a => a.Luid != luid));
        if (age < AdapterRefresh && !(unknown && age >= UnknownLuidRefresh))
        {
            return;
        }

        // LUIDs change when an adapter is reset or re-enabled; names are re-read now and then, never guessed.
        _adapters = DxgiAdapters.Enumerate(out _adapterError);
        _adaptersReadAt = Stopwatch.GetTimestamp();
    }
}
