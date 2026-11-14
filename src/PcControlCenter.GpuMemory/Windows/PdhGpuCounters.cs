using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using static PcControlCenter.GpuMemory.Windows.NativeMethods;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>
/// The WDDM GPU memory counters through one PDH query (the data Task Manager's GPU columns come from). Only the reader
/// thread uses an instance. Counters are added by their English names, so this works on a Korean Windows as well.
/// </summary>
/// <remarks>
/// Per process, <c>Local Usage</c> / <c>Non Local Usage</c> are preferred: <c>Dedicated Usage</c> / <c>Shared Usage</c>
/// also count surfaces a process can see but another process owns, so the same bytes appear under several processes
/// (gputaskman observed dwm.exe at about 15 GiB with the older counter and about 1.4 GiB with Local Usage on a 16 GB card).
/// The pair is chosen together so both columns always mean the same kind of accounting.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PdhGpuCounters : IDisposable
{
    public const string LocalUsage = @"\GPU Process Memory(*)\Local Usage";
    public const string NonLocalUsage = @"\GPU Process Memory(*)\Non Local Usage";
    public const string DedicatedUsage = @"\GPU Process Memory(*)\Dedicated Usage";
    public const string SharedUsage = @"\GPU Process Memory(*)\Shared Usage";
    public const string TotalCommitted = @"\GPU Process Memory(*)\Total Committed";
    public const string AdapterDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    public const string AdapterShared = @"\GPU Adapter Memory(*)\Shared Usage";
    public const string EngineUtilization = @"\GPU Engine(*)\Utilization Percentage";
    private const string Source = "Windows PDH";

    private readonly TimeProvider _time;
    private nint _query;
    private nint _processDedicated;
    private nint _processShared;
    private nint _committed;
    private nint _adapterDedicated;
    private nint _adapterShared;
    private nint _engineQuery;
    private nint _engine;
    private bool _engineCollected;

    private PdhGpuCounters(nint query, TimeProvider time)
    {
        _query = query;
        _time = time;
    }

    public ProcessCounterVariant Variant { get; private set; }
    public bool CommittedAvailable => _committed != 0;
    public bool AdapterCountersAvailable => _adapterDedicated != 0 || _adapterShared != 0;
    public bool AnyAvailable => Variant != ProcessCounterVariant.Unavailable || CommittedAvailable || AdapterCountersAvailable;

    /// <summary>The engine counter (a rate) lives in a query of its own, collected only while engine activity is wanted.</summary>
    public bool EnginesAvailable => _engine != 0;

    /// <summary>Problems while adding counters (shown with every reading: the affected values stay N/A).</summary>
    public IReadOnlyList<SensorIssue> SetupIssues { get; private set; } = [];

    /// <summary>Opens the query and adds the counters that exist on this PC. Returns null only when PDH itself cannot open.</summary>
    public static PdhGpuCounters? Open(TimeProvider time, out string? error)
    {
        var status = PdhOpenQuery(null, 0, out var query);
        if (status != 0)
        {
            error = $"PDH 쿼리를 열 수 없습니다 (PDH 0x{status:X8}).";
            return null;
        }

        var counters = new PdhGpuCounters(query, time);
        counters.AddAll();
        error = null;
        return counters;
    }

    /// <summary>One collection. Null arrays are counters that are not available (or failed this time, with an issue).</summary>
    public CollectedCounters Collect(ICollection<SensorIssue> issues)
    {
        var status = PdhCollectQueryData(_query);
        var collectedAt = _time.GetUtcNow();
        if (status != 0)
        {
            issues.Add(new SensorIssue(Source, "collect", SensorStatus.Error, $"GPU 카운터를 수집하지 못했습니다 (PDH 0x{status:X8}). 이번 측정의 값은 N/A입니다."));
            return new CollectedCounters(collectedAt, null, null, null, null, null);
        }

        return new CollectedCounters(
            collectedAt,
            ReadArray(_processDedicated, "process.dedicated", issues),
            ReadArray(_processShared, "process.shared", issues),
            ReadArray(_committed, "process.committed", issues),
            ReadArray(_adapterDedicated, "adapter.dedicated", issues),
            ReadArray(_adapterShared, "adapter.shared", issues));
    }

    /// <summary>
    /// Engine utilization per instance. A rate needs two collections: the first one after a pause gives no values
    /// (<c>Baseline</c>), which is not a problem worth reporting.
    /// </summary>
    public (IReadOnlyList<EngineCounterItem>? Items, bool Baseline) CollectEngines(ICollection<SensorIssue> issues)
    {
        if (_engine == 0)
        {
            return (null, false);
        }

        var status = PdhCollectQueryData(_engineQuery);
        var first = !_engineCollected;
        _engineCollected = true;
        if (status != 0)
        {
            if (!first)
            {
                issues.Add(new SensorIssue(Source, "engine.collect", SensorStatus.Error, $"GPU 엔진 카운터를 수집하지 못했습니다 (PDH 0x{status:X8})."));
            }

            return (null, first);
        }

        return first ? (null, true) : (ReadDoubles(_engine, issues), false);
    }

    /// <summary>Called when engine activity is no longer wanted: the next collection starts a new baseline.</summary>
    public void PauseEngines() => _engineCollected = false;

    public void Dispose()
    {
        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
            _query = 0;
        }

        if (_engineQuery != 0)
        {
            _ = PdhCloseQuery(_engineQuery);
            _engineQuery = 0;
        }
    }

    private static unsafe List<EngineCounterItem>? ReadDoubles(nint counter, ICollection<SensorIssue> issues)
    {
        const uint format = PdhFmtDouble | PdhFmtNoCap100;
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, format, ref size, out var count, 0);
        if (status is PdhNoData or PdhCstatusNoInstance or PdhInvalidData || (status == 0 && count == 0))
        {
            return [];
        }

        for (var attempt = 0; attempt < 3 && status == PdhMoreData; attempt++)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                status = PdhGetFormattedCounterArray(counter, format, ref size, out count, (nint)buffer);
                if (status == PdhMoreData)
                {
                    continue;
                }

                if (status != 0)
                {
                    break;
                }

                var items = new List<EngineCounterItem>((int)count);
                var rows = (PdhFmtCounterValueItem*)buffer;
                for (var i = 0; i < count; i++)
                {
                    // Instances that appeared since the last collection have no rate yet: skipped, not reported.
                    if (rows[i].Value.CStatus is PdhCstatusValidData or PdhCstatusNewData && Marshal.PtrToStringUni(rows[i].Name) is { } name)
                    {
                        items.Add(new EngineCounterItem(name, rows[i].Value.DoubleValue));
                    }
                }

                return items;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        issues.Add(new SensorIssue(Source, "engine", SensorStatus.Error, $"GPU 엔진 카운터 값을 읽지 못했습니다 (PDH 0x{status:X8})."));
        return null;
    }

    private void AddAll()
    {
        var issues = new List<SensorIssue>();
        // The preferred pair first; if either half is missing, the fallback pair (never a mix of the two kinds).
        var local = TryAdd(LocalUsage, out var localStatus);
        var nonLocal = TryAdd(NonLocalUsage, out var nonLocalStatus);
        if (local != 0 && nonLocal != 0)
        {
            (_processDedicated, _processShared, Variant) = (local, nonLocal, ProcessCounterVariant.LocalNonLocal);
        }
        else
        {
            Remove(local);
            Remove(nonLocal);
            var dedicated = TryAdd(DedicatedUsage, out var dedicatedStatus);
            var shared = TryAdd(SharedUsage, out var sharedStatus);
            if (dedicated != 0 || shared != 0)
            {
                (_processDedicated, _processShared, Variant) = (dedicated, shared, ProcessCounterVariant.DedicatedShared);
                issues.Add(new SensorIssue(Source, "process.variant", SensorStatus.NotSupported,
                    $"Local Usage/Non Local Usage 카운터를 쓸 수 없어(PDH 0x{localStatus:X8}/0x{nonLocalStatus:X8}) Dedicated Usage/Shared Usage로 대신 읽습니다. "
                    + "이 카운터는 다른 프로세스와 공유된 리소스도 포함해, 같은 메모리가 여러 프로세스에 중복 집계될 수 있습니다."));
                if (dedicated == 0)
                {
                    issues.Add(Missing("process.dedicated", DedicatedUsage, dedicatedStatus));
                }

                if (shared == 0)
                {
                    issues.Add(Missing("process.shared", SharedUsage, sharedStatus));
                }
            }
            else
            {
                Variant = ProcessCounterVariant.Unavailable;
                issues.Add(new SensorIssue(Source, "process", SensorStatus.NotSupported,
                    $"프로세스별 GPU 메모리 카운터(GPU Process Memory)를 추가하지 못했습니다 (PDH 0x{localStatus:X8}, 0x{dedicatedStatus:X8}). "
                    + "WDDM 2.0 이상 드라이버가 없거나, 가상 머신·원격 환경에서 카운터가 제공되지 않을 수 있습니다."));
            }
        }

        _committed = TryAdd(TotalCommitted, out var committedStatus);
        if (_committed == 0 && Variant != ProcessCounterVariant.Unavailable)
        {
            issues.Add(Missing("process.committed", TotalCommitted, committedStatus));
        }

        _adapterDedicated = TryAdd(AdapterDedicated, out var adapterDedicatedStatus);
        _adapterShared = TryAdd(AdapterShared, out var adapterSharedStatus);
        if (_adapterDedicated == 0)
        {
            issues.Add(Missing("adapter.dedicated", AdapterDedicated, adapterDedicatedStatus));
        }

        if (_adapterShared == 0)
        {
            issues.Add(Missing("adapter.shared", AdapterShared, adapterSharedStatus));
        }

        if (PdhOpenQuery(null, 0, out var engineQuery) == 0)
        {
            _engineQuery = engineQuery;
            if (PdhAddEnglishCounter(engineQuery, EngineUtilization, 0, out var engine) == 0)
            {
                _engine = engine;
            }
            else
            {
                issues.Add(new SensorIssue(Source, "engine", SensorStatus.NotSupported, $"카운터 {EngineUtilization}를 추가하지 못했습니다. 엔진 사용률은 N/A입니다."));
            }
        }

        SetupIssues = issues;
        // Baseline collection (the memory counters are raw values, so the first real reading is already valid).
        _ = PdhCollectQueryData(_query);
    }

    private nint TryAdd(string path, out uint status)
    {
        status = PdhAddEnglishCounter(_query, path, 0, out var counter);
        return status == 0 ? counter : 0;
    }

    private static void Remove(nint counter)
    {
        if (counter != 0)
        {
            _ = PdhRemoveCounter(counter);
        }
    }

    private static SensorIssue Missing(string scope, string path, uint status)
        => new(Source, scope, SensorStatus.NotSupported, $"카운터 {path}를 추가하지 못했습니다 (PDH 0x{status:X8}). 이 값은 N/A로 표시합니다.");

    private static unsafe List<CounterItem>? ReadArray(nint counter, string scope, ICollection<SensorIssue> issues)
    {
        if (counter == 0)
        {
            return null;
        }

        const uint format = PdhFmtLarge | PdhFmtNoCap100;
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, format, ref size, out var count, 0);
        if (status is PdhNoData or PdhCstatusNoInstance || (status == 0 && count == 0))
        {
            return [];
        }

        // The arrays describe the last collection, so the size cannot change between the two calls; the loop only guards it.
        for (var attempt = 0; attempt < 3 && status == PdhMoreData; attempt++)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                status = PdhGetFormattedCounterArray(counter, format, ref size, out count, (nint)buffer);
                if (status == PdhMoreData)
                {
                    continue;
                }

                if (status != 0)
                {
                    break;
                }

                var items = new List<CounterItem>((int)count);
                var invalid = 0;
                var rows = (PdhFmtCounterValueItem*)buffer;
                for (var i = 0; i < count; i++)
                {
                    if (rows[i].Value.CStatus is PdhCstatusValidData or PdhCstatusNewData && Marshal.PtrToStringUni(rows[i].Name) is { } name)
                    {
                        items.Add(new CounterItem(name, rows[i].Value.LargeValue));
                    }
                    else
                    {
                        invalid++;
                    }
                }

                if (invalid > 0)
                {
                    issues.Add(new SensorIssue(Source, scope, SensorStatus.Error, $"카운터 인스턴스 {invalid}개의 값이 유효하지 않아 제외했습니다."));
                }

                return items;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        issues.Add(new SensorIssue(Source, scope, SensorStatus.Error, $"카운터 값을 읽지 못했습니다 (PDH 0x{status:X8}). 이 값은 N/A입니다."));
        return null;
    }
}

/// <summary>The raw arrays of one collection and when Windows collected them.</summary>
internal sealed record CollectedCounters(
    DateTimeOffset CollectedAt,
    IReadOnlyList<CounterItem>? ProcessDedicated,
    IReadOnlyList<CounterItem>? ProcessShared,
    IReadOnlyList<CounterItem>? ProcessCommitted,
    IReadOnlyList<CounterItem>? AdapterDedicated,
    IReadOnlyList<CounterItem>? AdapterShared);
