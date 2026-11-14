using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Presentation.Charts;
using PcControlCenter.GpuMemory;
using PcControlCenter.Hardware;

namespace PcControlCenter.Presentation;

/// <summary>A part of a bar: its share of the whole (0-1), color, label, and whether it is an estimate (drawn hatched).</summary>
public sealed record BarSegment(double Fraction, string ColorHex, string Label, bool IsEstimate = false);

/// <summary>A legend entry under a bar.</summary>
public sealed record LegendItem(string ColorHex, string Label, string Value, bool IsEstimate = false, string? ToolTip = null);

/// <summary>One adapter in the header: name, local and system-RAM memory bars, engine activity, NVML for comparison.</summary>
public sealed record AdapterView(
    string Name,
    string Meta,
    IReadOnlyList<BarSegment> DedicatedSegments,
    string DedicatedText,
    IReadOnlyList<LegendItem> Legend,
    string? LegendNote,
    IReadOnlyList<BarSegment> SharedSegments,
    string SharedText,
    string Utilization,
    string UtilizationLabel,
    string Engines,
    string? Nvml,
    bool HasCapacity);

/// <summary>
/// One adapter's place in the header. The slot stays while its adapter does and only its <see cref="View"/> changes, so
/// the strip is not rebuilt on every reading (an open tooltip stays open).
/// </summary>
public sealed partial class AdapterSlot : ObservableObject
{
    public AdapterSlot(long luid, AdapterView view)
    {
        Luid = luid;
        View = view;
    }

    public long Luid { get; }

    [ObservableProperty] public partial AdapterView View { get; set; }
}

/// <summary>A labelled value in the detail panel.</summary>
public sealed record DetailFact(string Label, string Value, bool IsEstimate = false, string? ToolTip = null);

/// <summary>A model file mapped into the selected process.</summary>
public sealed record ModelRow(string Name, string Summary, string Size, string Evidence);

/// <summary>The selected process: numbers, what is loaded, an estimated split for LLMs, Strata's report (null unless it is Strata's), a short note.</summary>
public sealed record ProcessDetailView(
    string Title,
    string Pid,
    string Kind,
    string KindColor,
    bool IsStrata,
    string Subtitle,
    string Dedicated,
    string Shared,
    string Committed,
    string Gpu,
    string Engines,
    IReadOnlyList<BarSegment> Breakdown,
    IReadOnlyList<LegendItem> BreakdownLegend,
    string? BreakdownNote,
    string Runtimes,
    IReadOnlyList<ModelRow> Models,
    string? StrataModel,
    string? StrataWindow,
    string? StrataSlots,
    string? StrataEvidence,
    string Note,
    ChartModel? Chart);

/// <summary>The local AI module's view of Strata for this page: its latest poll, or why there is none.</summary>
public sealed record StrataContext(RuntimeSnapshot? Snapshot, string? Unavailable);

/// <summary>One row of the process table. Updated in place on every reading, so selection and sort order survive refreshes.</summary>
public sealed partial class GpuProcessRow(ProcessKey key, long luid) : ObservableObject
{
    public ProcessKey Key { get; } = key;
    public long Luid { get; } = luid;

    [ObservableProperty] public partial int Pid { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial ProcessKind Kind { get; set; }
    [ObservableProperty] public partial string KindLabel { get; set; } = "";
    [ObservableProperty] public partial string KindColor { get; set; } = GpuMemoryViewModel.KindColors[ProcessKind.App];
    [ObservableProperty] public partial string KindBasis { get; set; } = "";

    /// <summary>Bytes for sorting; -1 = N/A (sorts last when the largest come first).</summary>
    [ObservableProperty] public partial long Dedicated { get; set; }
    [ObservableProperty] public partial string DedicatedText { get; set; } = "";

    /// <summary>Share of the adapter's dedicated capacity (0-1), for the bar behind the number.</summary>
    [ObservableProperty] public partial double DedicatedShare { get; set; }
    [ObservableProperty] public partial long Shared { get; set; }
    [ObservableProperty] public partial string SharedText { get; set; } = "";
    [ObservableProperty] public partial long Committed { get; set; }
    [ObservableProperty] public partial string CommittedText { get; set; } = "";

    /// <summary>Busiest engine in percent; -1 = not read.</summary>
    [ObservableProperty] public partial double Gpu { get; set; }
    [ObservableProperty] public partial string GpuText { get; set; } = "";
    [ObservableProperty] public partial string Runtimes { get; set; } = "";
    [ObservableProperty] public partial string Inside { get; set; } = "";
    [ObservableProperty] public partial string Adapter { get; set; } = "";
    [ObservableProperty] public partial bool IsStrata { get; set; }
    /// <summary>Why the row's identity is uncertain; null when it was verified (no tooltip then).</summary>
    [ObservableProperty] public partial string? Identity { get; set; }
    [ObservableProperty] public partial bool IsUncertain { get; set; }
}

/// <summary>
/// Hardware › GPU 메모리. The layout follows the tools people already use for this (Task Manager's GPU page and Details
/// columns, gputaskman, nvtop): a device strip with a memory bar split by process kind, a dense sortable process table,
/// and a detail panel for the selected process. Values are measurements unless marked: estimates are labelled "추정" and
/// drawn hatched, Strata's values are grouped as reported by Strata, and the help panel explains the rest.
/// </summary>
public sealed partial class GpuMemoryViewModel : ObservableObject
{
    public static readonly TimeSpan ChartWindow = TimeSpan.FromMinutes(15);

    /// <summary>A Strata poll older than this is not used to link processes (the port may belong to someone else by then).</summary>
    public static readonly TimeSpan StrataMaxAge = TimeSpan.FromSeconds(15);

    public static readonly IReadOnlyDictionary<ProcessKind, string> KindColors = new Dictionary<ProcessKind, string>
    {
        [ProcessKind.Llm] = "#D55181",
        [ProcessKind.Ai] = "#9085E9",
        [ProcessKind.Compute] = "#D95926",
        [ProcessKind.Graphics3D] = "#199E70",
        [ProcessKind.Video] = "#C98500",
        [ProcessKind.Browser] = "#3987E5",
        [ProcessKind.Compositor] = "#8A8984",
        [ProcessKind.System] = "#6E6D68",
        [ProcessKind.Kernel] = "#6E6D68",
        [ProcessKind.App] = "#4FA3C7",
    };

    public static readonly IReadOnlyDictionary<ProcessKind, string> KindLabels = new Dictionary<ProcessKind, string>
    {
        [ProcessKind.Llm] = "LLM",
        [ProcessKind.Ai] = "AI",
        [ProcessKind.Compute] = "연산",
        [ProcessKind.Graphics3D] = "3D",
        [ProcessKind.Video] = "영상",
        [ProcessKind.Browser] = "브라우저",
        [ProcessKind.Compositor] = "화면 합성",
        [ProcessKind.System] = "시스템",
        [ProcessKind.Kernel] = "커널",
        [ProcessKind.App] = "앱",
    };

    private const string UnattributedColor = "#4A4A46";
    private const string SharedColor = "#C98500";
    private const string WeightsColor = "#D55181";
    private const string KvColor = "#D95926";
    private const string RestColor = "#6E6D68";
    private static readonly double ChartGapDays = TimeSpan.FromSeconds(25).TotalDays;
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    private static readonly IReadOnlyDictionary<ProcessKind, string> KindNotes = new Dictionary<ProcessKind, string>
    {
        [ProcessKind.Llm] = "가중치·KV 캐시·계산 버퍼·CUDA 컨텍스트가 함께 들어 있습니다. 런타임이 바이트를 보고하지 않는 한 나눈 값은 추정입니다.",
        [ProcessKind.Ai] = "ML 런타임의 텐서와 작업 공간입니다. 밖에서는 내부 할당을 나눠 볼 수 없습니다.",
        [ProcessKind.Compute] = "CUDA·HIP 할당입니다. 내용은 Nsight 같은 런타임 도구로만 볼 수 있습니다.",
        [ProcessKind.Graphics3D] = "텍스처·메시·렌더 타깃입니다. 리소스 이름은 PIX·Nsight 같은 그래픽 디버거가 필요합니다.",
        [ProcessKind.Video] = "영상 디코드·인코드 프레임과 코덱 버퍼입니다.",
        [ProcessKind.Browser] = "페이지 타일·이미지·영상 프레임·WebGL/WebGPU 버퍼입니다.",
        [ProcessKind.Compositor] = "창 버퍼와 합성된 화면입니다. 다른 앱의 표면이 함께 집계될 수 있습니다.",
        [ProcessKind.System] = "커서·디스플레이 표면을 가진 세션 프로세스입니다.",
        [ProcessKind.Kernel] = "드라이버·커널 할당입니다 (페이징 버퍼, 화면 출력 등).",
        [ProcessKind.App] = "창 버퍼와 UI 표면입니다.",
    };

    private readonly GpuMemoryMonitor _monitor;
    private readonly SharedHardwareHub _hub;
    private readonly Func<StrataContext> _strata;
    private readonly TimeProvider _time;
    private readonly Dictionary<(ProcessKey, long), GpuProcessRow> _rows = [];
    private GpuMemorySnapshot? _current;
    private ServerProcessLink? _link;
    private RuntimeSnapshot? _strataSnapshot;
    private bool _rendered;
    private DateTimeOffset? _renderedAt;
    private bool _renderedStale;
    private string? _renderedKey;

    public GpuMemoryViewModel(GpuMemoryMonitor monitor, SharedHardwareHub hub, Func<StrataContext> strata, TimeProvider? time = null)
    {
        _monitor = monitor;
        _hub = hub;
        _strata = strata;
        _time = time ?? TimeProvider.System;
    }

    public ObservableCollection<AdapterSlot> Adapters { get; } = [];

    /// <summary>The visible rows (filter and "전체" applied); the view sorts them.</summary>
    public ObservableCollection<GpuProcessRow> Processes { get; } = [];

    public ObservableCollection<string> Issues { get; } = [];

    [ObservableProperty] public partial GpuProcessRow? SelectedProcess { get; set; }
    [ObservableProperty] public partial ProcessDetailView? Detail { get; set; }
    [ObservableProperty] public partial Level StatusLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string StatusToolTip { get; set; } = "";
    [ObservableProperty] public partial bool IsMock { get; set; }

    /// <summary>Shown over the table instead of rows (no reading yet, stale, unavailable, switched off).</summary>
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial string MessageDetail { get; set; } = "";
    [ObservableProperty] public partial bool HasValues { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = "";

    /// <summary>Why the table is empty while there are values (nothing above the listing threshold, or the filter matches nothing).</summary>
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial bool ShowGpuColumn { get; set; }
    [ObservableProperty] public partial string StrataText { get; set; } = "";
    [ObservableProperty] public partial Level StrataLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string? StrataToolTip { get; set; }

    /// <summary>Holds the table and the detail panel on the values they show (the reader keeps reading).</summary>
    [ObservableProperty] public partial bool IsPaused { get; set; }

    /// <summary>Also list processes below 1 MiB local / 16 MiB shared or committed.</summary>
    [ObservableProperty] public partial bool ShowAll { get; set; }

    /// <summary>Name or PID.</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial bool HelpOpen { get; set; }

    /// <summary>The page is on screen: the reader then reads every 2 s and includes engines and loaded files.</summary>
    [ObservableProperty] public partial bool IsVisible { get; set; }

    public IReadOnlyList<LegendItem> ValueKinds { get; } =
    [
        new("#F0EFEC", "측정", "Windows·드라이버가 보고한 값 (별도 표시 없음)"),
        new("#F0EFEC", "계산", "측정값의 합·차 (예: 나뉘지 않음)"),
        new("#898781", "추정", "모델 파일 헤더와 컨텍스트로 계산한 값 (빗금, \"추정\" 표시)", IsEstimate: true),
        new("#F0EFEC", "Strata 보고", "Strata가 자체 API로 보고한 값"),
        new("#898781", "N/A", "측정할 수 없거나 보고되지 않음 (0이 아님)"),
    ];

    public IReadOnlyList<DetailFact> Knowable { get; } =
    [
        new("GPU 전체 사용량", "WDDM 어댑터 카운터, NVML — 측정"),
        new("프로세스별 VRAM·공유", "WDDM 프로세스 카운터 (Local / Non Local Usage) — 측정"),
        new("엔진 사용률", "WDDM 엔진 카운터 — 측정. CUDA 작업이 3D 엔진에 보일 수 있음"),
        new("런타임", "프로세스에 불러온 DLL 이름 — 감지 (GPU 작업의 증거는 아님)"),
        new("모델 파일", "프로세스에 매핑된 파일과 디스크의 GGUF 헤더 — 확인"),
        new("가중치·KV 캐시", "헤더 구조 × 컨텍스트 — 추정 (런타임 로그나 API가 있어야 정확)"),
        new("텍스처·텐서 이름", "그래픽 디버거(PIX, Nsight)가 필요 — 알 수 없음"),
    ];

    public IReadOnlyList<string> Checks { get; } =
    [
        "작업 관리자 › 세부 정보 › 열 선택 › 전용·공유 GPU 메모리",
        "성능 모니터 › GPU Process Memory › Local Usage (이 화면이 읽는 카운터)",
        "nvidia-smi --query-gpu=memory.used,memory.total --format=csv (장치 전체)",
    ];

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ToggleHelp() => HelpOpen = !HelpOpen;

    partial void OnIsVisibleChanged(bool value)
    {
        if (value)
        {
            // The port first, so the immediate reading that SetActive triggers already identifies the Strata processes.
            UpdateWatchedPorts(_strata(), _time.GetUtcNow());
            _monitor.SetActive(true);
            Refresh();
        }
        else
        {
            _monitor.SetActive(false);
        }
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            _rendered = false;
            Refresh();
        }
        else
        {
            StatusText = $"일시정지 · {Text.Clock(_renderedAt)} 값";
            StatusLevel = Level.Muted;
        }
    }

    partial void OnShowAllChanged(bool value) => RebuildRows(force: true);

    partial void OnFilterChanged(string value) => RebuildRows(force: true);

    partial void OnSelectedProcessChanged(GpuProcessRow? value) => BuildDetail();

    /// <summary>A reading older than three intervals plus two seconds is not shown as current.</summary>
    public static TimeSpan StaleAfter(GpuMemorySnapshot snapshot)
        => (snapshot.ExpectedInterval > TimeSpan.Zero ? snapshot.ExpectedInterval : GpuMemoryMonitor.DefaultBackgroundInterval) * 3 + TimeSpan.FromSeconds(2);

    /// <summary>The port of a reachable, recently polled, local Strata server; null otherwise (nothing is linked then).</summary>
    public static int? StrataPort(StrataContext strata, DateTimeOffset now)
    {
        if (strata.Snapshot is not { IsReachable: true } snapshot || now - snapshot.Timestamp > StrataMaxAge
            || !Uri.TryCreate(snapshot.Endpoint, UriKind.Absolute, out var uri) || !ServerProcessLinker.IsLocalHost(uri.Host))
        {
            return null;
        }

        return uri.Port;
    }

    /// <summary>Called every second while the page is visible. Cheap: readings come from the reader thread.</summary>
    public void Refresh()
    {
        var now = _time.GetUtcNow();
        var health = _monitor.Health;
        var latest = _monitor.Latest;
        var strata = _strata();
        UpdateWatchedPorts(strata, now);
        var age = latest is null ? (TimeSpan?)null : now - latest.Timestamp;
        // Stale by age, or because no further readings can come (reader stopped, unavailable or switched off).
        var stale = latest is not null && (health.State is GpuMemoryMonitorState.Stopped or GpuMemoryMonitorState.Unavailable or GpuMemoryMonitorState.Disabled
                                           || age > StaleAfter(latest));
        if (IsPaused && _rendered)
        {
            return;
        }

        UpdateStatus(health, latest, age, stale);
        var key = $"{strata.Snapshot?.State}|{strata.Snapshot?.Endpoint}|{strata.Snapshot?.Model?.Id}|{strata.Snapshot?.Model?.ContextTokens}|{StrataPort(strata, now)}";
        if (_rendered && latest?.Timestamp == _renderedAt && stale == _renderedStale && key == _renderedKey)
        {
            UpdateStrata(strata, now);
            return;
        }

        _rendered = true;
        _renderedAt = latest?.Timestamp;
        _renderedStale = stale;
        _renderedKey = key;
        IsMock = latest?.IsMock ?? health.IsMock;
        _current = latest is not null && !stale ? latest : null;
        _strataSnapshot = strata.Snapshot;
        _link = _current is not null && StrataPort(strata, now) is not null ? ServerProcessLinker.Link(strata.Snapshot!.Endpoint, _current) : null;
        UpdateStrata(strata, now);
        HasValues = _current is not null;
        Replace(Issues, [.. (latest?.Issues ?? []).Select(i => $"{i.Source} [{i.Scope}]: {i.Message}").Distinct()]);
        (Message, MessageDetail) = health.State switch
        {
            GpuMemoryMonitorState.Disabled => ("프로세스별 GPU 메모리 읽기가 꺼져 있습니다", "설정 › 공통 › 공통 센서에서 켤 수 있습니다."),
            GpuMemoryMonitorState.Unavailable => ("Windows GPU 카운터를 읽을 수 없습니다", health.UnavailableReason ?? ""),
            _ when latest is null => ("첫 측정을 기다리는 중", ""),
            _ when stale => ("새 측정값이 없습니다", $"마지막 측정 {Text.Clock(latest.Timestamp)} · 오래된 값은 표시하지 않습니다."),
            _ => ("", ""),
        };
        SyncAdapters(_current is null ? [] : BuildAdapters(_current));
        RebuildRows(force: true);
    }

    private void UpdateStatus(GpuMemoryHealth health, GpuMemorySnapshot? latest, TimeSpan? age, bool stale)
    {
        (StatusLevel, StatusText) = health.State switch
        {
            GpuMemoryMonitorState.Disabled => (Level.Muted, "꺼짐"),
            GpuMemoryMonitorState.Unavailable => (Level.Warning, "사용 불가"),
            _ when latest is null => (Level.Muted, "측정 대기"),
            _ when stale => (Level.Error, $"새 측정 없음 · 마지막 {Text.Clock(latest.Timestamp)}"),
            _ => (latest.IsMock || latest.Variant != ProcessCounterVariant.LocalNonLocal ? Level.Warning : Level.Good,
                $"{Text.Clock(latest.Timestamp)} · {health.Interval.TotalSeconds:0.#}초마다"),
        };
        var lines = new List<string> { $"출처: {latest?.Source ?? health.ProviderName}" };
        if (latest is not null)
        {
            lines.Add(latest.Variant switch
            {
                ProcessCounterVariant.LocalNonLocal => "프로세스 값: Local Usage / Non Local Usage",
                ProcessCounterVariant.DedicatedShared => "프로세스 값: Dedicated Usage / Shared Usage (대체 카운터 — 공유 리소스가 중복 집계될 수 있음)",
                _ => "프로세스 값: 읽을 수 없음",
            });
            lines.Add($"측정 {Text.DateTime(latest.Timestamp)} ({Text.Age(age ?? TimeSpan.Zero)}) · 한 번 읽는 데 {latest.CollectionTime.TotalMilliseconds:0} ms");
        }

        if (health.Failures > 0)
        {
            lines.Add($"읽기 예외 {health.Failures}회 — {health.LastError}");
        }

        lines.AddRange((latest?.Issues ?? []).Select(i => $"• {i.Message}"));
        StatusToolTip = string.Join(Environment.NewLine, lines);
    }

    private void UpdateStrata(StrataContext strata, DateTimeOffset now)
    {
        if (strata.Snapshot is not { } rt)
        {
            (StrataLevel, StrataText, StrataToolTip) = (Level.Muted, "Strata 꺼짐", strata.Unavailable);
            return;
        }

        var port = StrataPort(strata, now);
        var holder = _link is { IsLinked: true } link
            ? link.Processes.FirstOrDefault(p => p.Memory.Any(m => m.DedicatedBytes > 0))?.Process ?? link.Listener
            : null;
        StrataLevel = !rt.IsReachable ? Level.Muted : port is null ? Level.Warning : Level.Good;
        StrataText = !rt.IsReachable ? $"Strata {RuntimeStateText.ToKorean(rt.State)}"
            : holder is not null ? $"Strata · {holder.Name}"
            : "Strata 연결됨";
        StrataToolTip = string.Join(Environment.NewLine, new[]
        {
            $"{RuntimeStateText.ToKorean(rt.State)} · {rt.Endpoint} · 조회 {Text.Clock(rt.Timestamp)}",
            rt.IsReachable && port is null ? "이 PC의 주소가 아니거나 조회가 오래되어 프로세스와 연결하지 않습니다." : null,
            _link is { IsLinked: false, Reason: { } reason } ? reason : null,
            _link is { IsLinked: true } l ? string.Join(Environment.NewLine, l.Evidence) : null,
        }.Where(x => x is not null));
    }

    private void UpdateWatchedPorts(StrataContext strata, DateTimeOffset now)
        => _monitor.SetWatchedPorts(StrataPort(strata, now) is int port ? [port] : []);

    private void SyncAdapters(IReadOnlyList<(long Luid, AdapterView View)> views)
    {
        if (Adapters.Count == views.Count && Adapters.Select(a => a.Luid).SequenceEqual(views.Select(v => v.Luid)))
        {
            for (var i = 0; i < views.Count; i++)
            {
                Adapters[i].View = views[i].View;
            }

            return;
        }

        Adapters.Clear();
        foreach (var (luid, view) in views)
        {
            Adapters.Add(new AdapterSlot(luid, view));
        }
    }

    private List<(long Luid, AdapterView View)> BuildAdapters(GpuMemorySnapshot s)
    {
        var nvidia = s.Adapters.Count(a => a.Info is { IsNvidia: true, IsSoftware: false });
        var devices = _hub.GpuDevices;
        var hubSample = _hub.Latest?.Snapshot;
        var hubFresh = hubSample is not null && _time.GetUtcNow() - hubSample.Timestamp <= _hub.Health.Interval * 2 + TimeSpan.FromSeconds(1);
        var views = new List<(long, AdapterView)>();
        foreach (var adapter in s.Adapters)
        {
            var info = adapter.Info;
            var capacity = info?.DedicatedVideoMemoryBytes ?? 0;
            var rows = s.Processes.Where(p => p.Luid == adapter.Luid).ToList();
            var byKind = rows.Where(p => p.DedicatedBytes > 0)
                .GroupBy(p => KindOf(s, p).Kind)
                .Select(g => (Kind: g.Key, Bytes: g.Sum(p => p.DedicatedBytes!.Value)))
                .OrderByDescending(x => x.Bytes)
                .ToList();
            long? processSum = rows.All(p => p.DedicatedBytes is not null) ? rows.Sum(p => p.DedicatedBytes ?? 0) : null;
            var used = adapter.DedicatedUsageBytes;
            long? unattributed = used is long u && processSum is long sum ? u - sum : null;
            var scale = capacity > 0 ? capacity : Math.Max(1, used ?? byKind.Sum(k => k.Bytes));
            var parts = byKind.Select(k => (Color: KindColors[k.Kind], Label: KindLabels[k.Kind], k.Bytes)).ToList();
            if (unattributed > 0)
            {
                parts.Add((UnattributedColor, "나뉘지 않음", unattributed.Value));
            }

            // When the process rows add up to more than the adapter reports (shared surfaces counted twice), the bar keeps the
            // proportions within the adapter's usage; the legend keeps the raw numbers.
            var partsTotal = parts.Sum(p => p.Bytes);
            var factor = used is long usedBytes && partsTotal > usedBytes && partsTotal > 0 ? (double)usedBytes / partsTotal : 1.0;
            var segments = parts.Select(p => new BarSegment(Math.Clamp(p.Bytes * factor / scale, 0, 1), p.Color, p.Label)).ToList();
            var legend = parts.Select(p => new LegendItem(p.Color, p.Label, Text.Bytes(p.Bytes))).ToList();
            string? legendNote = unattributed < 0 ? $"프로세스 합이 어댑터 사용량보다 {Text.Bytes(-unattributed)} 큽니다 (공유 리소스 중복 집계 가능)" : null;
            var shared = adapter.SharedUsageBytes;
            var sharedCapacity = info?.SharedSystemMemoryBytes ?? 0;
            string? nvml = null;
            if (info is { IsNvidia: true, IsSoftware: false } && nvidia == 1 && devices.Count == 1 && hubFresh && hubSample!.Gpu(devices[0].Index) is { } gpu)
            {
                nvml = $"NVML {Pair(gpu.MemoryUsedBytes, gpu.MemoryTotalBytes)}";
            }

            var engines = adapter.Engines;
            var meta = info is null ? "DXGI 목록에 없는 LUID"
                : info.VendorName + (info.IsSoftware ? " · 소프트웨어" : "") + (info is { IsSoftware: false } && capacity <= 512L << 20 ? " · 통합 GPU일 수 있음" : "");
            views.Add((adapter.Luid, new AdapterView(
                info?.Name ?? $"어댑터 {GpuLuid.Format(adapter.Luid)}",
                meta,
                segments,
                capacity > 0 ? Pair(used, capacity) : Text.Bytes(used),
                legend,
                legendNote,
                sharedCapacity > 0 && shared is long sh ? [new BarSegment(Math.Clamp((double)sh / sharedCapacity, 0, 1), SharedColor, "공유")] : [],
                sharedCapacity > 0 ? Pair(shared, sharedCapacity) : Text.Bytes(shared),
                engines is null ? "" : $"{engines.Busiest:0}%",
                engines is null ? "" : BusiestEngine(engines),
                engines is null ? "" : EnginesText(engines),
                nvml,
                capacity > 0)));
        }

        return views;
    }

    private void RebuildRows(bool force)
    {
        if (IsPaused && !force)
        {
            return;
        }

        var s = _current;
        if (s is null)
        {
            Processes.Clear();
            _rows.Clear();
            CountText = "";
            EmptyText = "";
            ShowGpuColumn = false;
            SelectedProcess = null;
            Detail = null;
            return;
        }

        var filter = Filter.Trim();
        var listed = s.Processes.Where(p => ShowAll || GpuMemoryRules.IsListed(p)).ToList();
        var visible = listed.Where(p => filter.Length == 0 || Matches(s, p, filter)).ToList();
        var keys = new HashSet<(ProcessKey, long)>();
        foreach (var p in visible)
        {
            var key = (KeyOf(s, p), p.Luid);
            keys.Add(key);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new GpuProcessRow(key.Item1, p.Luid);
                _rows[key] = row;
                Processes.Add(row);
            }

            Update(row, s, p);
        }

        foreach (var gone in _rows.Where(r => !keys.Contains(r.Key)).ToList())
        {
            _rows.Remove(gone.Key);
            Processes.Remove(gone.Value);
        }

        var hidden = s.Processes.Count - listed.Count;
        CountText = $"프로세스 {visible.Count}" + (hidden > 0 && !ShowAll ? $" · 작은 프로세스 {hidden}개 숨김" : "");
        EmptyText = visible.Count > 0 ? ""
            : filter.Length > 0 ? $"'{filter}'와 맞는 프로세스가 없습니다"
            : hidden > 0 ? $"GPU 메모리를 1 MiB 넘게 쓰는 프로세스가 없습니다 · '작은 프로세스 포함'으로 {hidden}개를 볼 수 있습니다"
            : "GPU 메모리를 쓰는 프로세스가 없습니다";
        ShowGpuColumn = visible.Select(p => p.Luid).Distinct().Count() > 1;
        if (SelectedProcess is null || !Processes.Contains(SelectedProcess))
        {
            SelectedProcess = Processes.OrderByDescending(r => r.Dedicated).FirstOrDefault();
        }
        else
        {
            BuildDetail();
        }
    }

    private void Update(GpuProcessRow row, GpuMemorySnapshot s, GpuProcessMemory p)
    {
        var entry = s.Process(p.ProcessId);
        var detail = s.Detail(p.ProcessId);
        var kind = KindOf(s, p);
        var model = detail?.Models.FirstOrDefault(m => m.Gguf is { Error: null })?.Gguf;
        var capacity = s.Adapter(p.Luid)?.Info?.DedicatedVideoMemoryBytes ?? 0;
        var strata = IsStrata(p.ProcessId);
        row.Pid = p.ProcessId;
        row.Name = entry?.Name ?? $"PID {p.ProcessId}";
        row.Kind = kind.Kind;
        row.KindLabel = KindLabels[kind.Kind];
        row.KindColor = KindColors[kind.Kind];
        row.KindBasis = $"분류 근거: {kind.Basis}";
        row.Dedicated = p.DedicatedBytes ?? -1;
        row.DedicatedText = Text.Bytes(p.DedicatedBytes);
        row.DedicatedShare = capacity > 0 && p.DedicatedBytes is long d ? Math.Clamp((double)d / capacity, 0, 1) : 0;
        row.Shared = p.SharedBytes ?? -1;
        row.SharedText = Text.Bytes(p.SharedBytes);
        row.Committed = p.CommittedBytes ?? -1;
        row.CommittedText = s.CommittedAvailable ? Text.Bytes(p.CommittedBytes) : Text.NA;
        row.Gpu = p.Engines?.Busiest ?? -1;
        row.GpuText = p.Engines is { } e ? e.Busiest.ToString("0", Ko) : "";
        row.Runtimes = detail is null ? "" : detail.AccessDenied ? "권한 없음" : string.Join(" · ", detail.Runtimes.Take(3));
        row.Inside = model is not null && kind.Kind is ProcessKind.Llm or ProcessKind.Ai
            ? string.Join(" · ", new[] { model.Name, ParamsLabel(model.Parameters), model.Quantization }.Where(x => !string.IsNullOrEmpty(x)))
            : strata && _strataSnapshot?.Model?.Id is { } id ? id
            : detail?.Models.FirstOrDefault()?.FileName ?? InsideOf(kind.Kind);
        row.Adapter = s.Adapter(p.Luid)?.Info?.Name ?? GpuLuid.Format(p.Luid);
        row.IsStrata = strata;
        row.IsUncertain = entry?.State is not ProcessIdentityState.Verified;
        row.Identity = IdentityText(entry) is { Length: > 0 } identity ? identity : null;
    }

    private void BuildDetail()
    {
        var s = _current;
        var row = SelectedProcess;
        var p = s is null || row is null ? null : s.Processes.FirstOrDefault(x => x.ProcessId == row.Pid && x.Luid == row.Luid);
        if (s is null || row is null || p is null)
        {
            Detail = null;
            return;
        }

        var entry = s.Process(p.ProcessId);
        var detail = s.Detail(p.ProcessId);
        var kind = KindOf(s, p);
        var engines = p.Engines;
        // An estimated split only for an LLM whose model file header was read; Strata's context when this is its process.
        var model = detail?.Models.FirstOrDefault(m => m.Gguf is { Kind: "LLM", Error: null })?.Gguf;
        var context = IsStrata(p.ProcessId) ? _strataSnapshot?.Model?.ContextTokens : null;
        var estimate = model is null ? null : LlmMemoryEstimate.From(model, p.DedicatedBytes, context);
        IReadOnlyList<BarSegment> breakdown = [];
        IReadOnlyList<LegendItem> breakdownLegend = [];
        string? breakdownNote = null;
        if (estimate is not null)
        {
            double Frac(long bytes) => estimate.Total > 0 ? (double)bytes / estimate.Total : 0;
            var segments = new List<BarSegment> { new(Frac(estimate.Weights), WeightsColor, "가중치", true) };
            var legend = new List<LegendItem> { new(WeightsColor, "가중치", Text.Bytes(estimate.Weights), true) };
            if (estimate.KvCache is long kv)
            {
                segments.Add(new(Frac(kv), KvColor, "KV 캐시", true));
                legend.Add(new(KvColor, estimate.KvIsUpperBound ? "KV 캐시 (상한)" : "KV 캐시", Text.Bytes(kv), true));
            }

            segments.Add(new(Frac(estimate.Rest), RestColor, "나머지", true));
            legend.Add(new(RestColor, estimate.KvCache is null ? "KV·버퍼·런타임" : "버퍼·런타임", Text.Bytes(estimate.Rest), true));
            breakdown = segments;
            breakdownLegend = legend;
            breakdownNote = string.Join(" ", new[]
            {
                "모든 층이 GPU에 있고 KV가 f16이라고 가정한 추정입니다.",
                estimate.WeightsCapped ? "가중치 전체가 이 프로세스의 VRAM보다 커서 일부만 GPU에 있을 수 있습니다." : null,
                estimate.KvCache is null ? (context is null ? "컨텍스트를 알 수 없어 KV 캐시는 나누지 않았습니다." : "KV 추정값이 남은 양보다 커서 나누지 않았습니다.") : null,
            }.Where(x => x is not null));
        }

        IReadOnlyList<ModelRow> models = (detail?.Models ?? []).Select(m => new ModelRow(
            m.Gguf?.Name ?? m.FileName,
            m.Gguf is { Error: null } g
                ? string.Join(" · ", new[]
                {
                    g.Architecture, ParamsLabel(g.Parameters), g.Quantization, g.Layers > 0 ? $"{g.Layers}층" : null,
                    g.TrainedContext is long tc ? $"학습 컨텍스트 {Text.Count(tc)}" : null,
                    g.KvBytesPerTokenF16 is long kvt ? $"KV {kvt / 1024} KiB/토큰" : null,
                }.Where(x => !string.IsNullOrEmpty(x)))
                : m.Gguf?.Error ?? m.Format,
            Text.Bytes(m.SizeBytes),
            m.FileName)).ToList();
        if (Detail is { } previous && previous.Models.SequenceEqual(models))
        {
            models = previous.Models; // same list: the rows (and an open tooltip) stay
        }

        string? strataModel = null, strataWindow = null, strataSlots = null, evidence = null;
        if (IsStrata(p.ProcessId) && _strataSnapshot is { } rt)
        {
            strataModel = rt.Model?.Id ?? Text.NA;
            strataWindow = rt.Model?.ContextTokens is long ctx ? $"{Text.Count(ctx)} 토큰" : Text.NA;
            var held = rt.Slots.Where(x => x.HeldTokens is not null).ToList();
            strataSlots = held.Count == 0 ? Text.NA : $"{Text.Count(held.Sum(x => x.HeldTokens!.Value))} (슬롯 {rt.Slots.Count})";
            evidence = _link is { IsLinked: true } l ? string.Join(Environment.NewLine, l.Evidence) : null;
        }

        Detail = new ProcessDetailView(
            row.Name,
            p.ProcessId.ToString(CultureInfo.InvariantCulture),
            KindLabels[kind.Kind],
            KindColors[kind.Kind],
            IsStrata(p.ProcessId),
            string.Join(" · ", new[]
            {
                entry?.StartTime is { } start ? $"시작 {Text.DateTime(start)}" : null,
                IdentityText(entry) is { Length: > 0 } id ? id : null,
                s.Adapter(p.Luid)?.Info?.Name,
                $"분류 근거: {kind.Basis}",
            }.Where(x => x is not null)),
            Text.Bytes(p.DedicatedBytes),
            Text.Bytes(p.SharedBytes),
            s.CommittedAvailable ? Text.Bytes(p.CommittedBytes) : Text.NA,
            engines is null ? Text.NA : $"{engines.Busiest:0}%",
            engines is null ? "" : EnginesText(engines),
            breakdown,
            breakdownLegend,
            breakdownNote,
            detail is null ? "아직 확인하지 않음" : detail.AccessDenied ? "확인할 수 없음 (보호되었거나 다른 사용자의 프로세스)"
                : detail.Runtimes.Count == 0 ? "알려진 GPU·AI 런타임 없음" : string.Join(" · ", detail.Runtimes) + (detail.Partial ? " (일부만 확인)" : ""),
            models,
            strataModel,
            strataWindow,
            strataSlots,
            evidence,
            KindNotes[kind.Kind],
            BuildChart(row, p));
    }

    private ChartModel BuildChart(GpuProcessRow row, GpuProcessMemory p)
    {
        var history = _monitor.History(ChartWindow);
        var processPoints = history
            .Select(h => (h.Timestamp, Value: h.Processes.FirstOrDefault(x => x.Key == row.Key && x.Luid == p.Luid).DedicatedBytes))
            .Where(x => x.Value is not null)
            .ToList();
        var adapterPoints = history
            .Select(h => (h.Timestamp, Value: h.Adapters.FirstOrDefault(a => a.Luid == p.Luid).DedicatedUsageBytes))
            .Where(x => x.Value is not null)
            .ToList();
        var series = new List<ChartSeries>();
        if (adapterPoints.Count > 0)
        {
            series.Add(new ChartSeries("GPU 전체", [.. adapterPoints.Select(x => x.Timestamp.LocalDateTime.ToOADate())],
                [.. adapterPoints.Select(x => x.Value!.Value / (1024.0 * 1024 * 1024))], "#8A8984"));
        }

        if (processPoints.Count > 0)
        {
            series.Add(new ChartSeries(row.Name, [.. processPoints.Select(x => x.Timestamp.LocalDateTime.ToOADate())],
                [.. processPoints.Select(x => x.Value!.Value / (1024.0 * 1024 * 1024))], row.KindColor));
        }

        return new ChartModel
        {
            Title = "전용 메모리 · 15분",
            Unit = "GiB",
            Series = series,
            XIsTime = true,
            GapThreshold = ChartGapDays,
            YMin = 0,
            ValueFormat = "0.00",
            EmptyText = "기록 없음",
        };
    }

    private KindResult KindOf(GpuMemorySnapshot s, GpuProcessMemory p)
        => ProcessKinds.Classify(p.ProcessId, s.Process(p.ProcessId)?.Name ?? "", s.Detail(p.ProcessId), p.Engines, IsStrata(p.ProcessId));

    private bool IsStrata(int pid) => _link is { IsLinked: true } link && link.Processes.Any(x => x.Process.ProcessId == pid);

    private static ProcessKey KeyOf(GpuMemorySnapshot s, GpuProcessMemory p) => s.Process(p.ProcessId)?.Key ?? new ProcessKey(p.ProcessId, 0, $"PID {p.ProcessId}");

    private static bool Matches(GpuMemorySnapshot s, GpuProcessMemory p, string filter)
        => p.ProcessId.ToString(CultureInfo.InvariantCulture) == filter
           || (s.Process(p.ProcessId)?.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string IdentityText(ProcessEntry? entry) => entry?.State switch
    {
        ProcessIdentityState.Verified => "",
        ProcessIdentityState.StartTimeUnavailable => "시작 시각 확인 불가",
        ProcessIdentityState.Exited => "측정 직후 종료됨",
        ProcessIdentityState.ReusedAfterSample => "PID 재사용됨 (값은 이전 프로세스)",
        _ => "프로세스 정보 없음",
    };

    private static string InsideOf(ProcessKind kind) => kind switch
    {
        ProcessKind.Compositor => "창 버퍼·합성 화면",
        ProcessKind.Browser => "페이지·영상 텍스처",
        ProcessKind.Video => "영상 프레임",
        ProcessKind.Graphics3D => "텍스처·렌더 타깃",
        ProcessKind.Kernel => "드라이버 할당",
        _ => "",
    };

    private static string ParamsLabel(long parameters) => parameters >= 1_000_000_000 ? (parameters / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B"
        : parameters >= 1_000_000 ? (parameters / 1e6).ToString("0", CultureInfo.InvariantCulture) + "M"
        : parameters > 0 ? parameters.ToString(CultureInfo.InvariantCulture) : "";

    private static string EnginesText(EngineUsage e) => $"3D {e.ThreeD:0} · 연산 {e.Compute:0} · 복사 {e.Copy:0} · 영상 {e.Video:0}";

    /// <summary>"GPU" plus the busiest engine group, as Task Manager's GPU column counts it (the busiest engine).</summary>
    private static string BusiestEngine(EngineUsage e)
    {
        if (e.Busiest < 1)
        {
            return "GPU";
        }

        var name = e.Busiest == e.Compute ? "연산" : e.Busiest == e.ThreeD ? "3D" : e.Busiest == e.Video ? "영상" : e.Busiest == e.Copy ? "복사" : "기타";
        return $"GPU · {name}";
    }

    private static string Pair(long? used, long? total) => used is null && total is null ? Text.NA : $"{Text.Bytes(used)} / {Text.Bytes(total)}";

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
