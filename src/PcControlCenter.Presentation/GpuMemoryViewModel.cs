using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Presentation.Charts;
using PcControlCenter.GpuMemory;
using PcControlCenter.Hardware;

namespace PcControlCenter.Presentation;

/// <summary>One labelled value with where it comes from (<see cref="ValueOrigin"/>) and what it does not mean.</summary>
public sealed record MemoryValueRow(string Label, string Value, string Origin, string Note, Level Level = Level.Muted);

/// <summary>One GPU adapter: its Windows usage counters, NVML for comparison and what the process rows add up to.</summary>
public sealed record GpuAdapterCard(string Title, string Subtitle, IReadOnlyList<MemoryValueRow> Rows, string? Note);

/// <summary>One process on one adapter. Every byte value is measured by Windows; identity says how sure the PID is.</summary>
public sealed record GpuProcessRow(
    string Pid,
    string Name,
    string Adapter,
    string Dedicated,
    string Shared,
    string Committed,
    string Started,
    string Identity,
    string Tag,
    Level Level);

public sealed record OriginLegendRow(string Tag, string Meaning);

/// <summary>The local AI module's view of Strata for this page: its latest poll, or why there is none.</summary>
public sealed record StrataContext(RuntimeSnapshot? Snapshot, string? Unavailable);

/// <summary>
/// Hardware › GPU 메모리: where GPU memory goes on this PC, as far as Windows and the runtimes report it. Adapter usage and
/// per-process usage are Windows' measurements; sums and differences are marked as computed; Strata's model, context and
/// held tokens are marked as runtime-reported. Nothing on this page splits a process's memory into weights, KV cache or
/// buffers: no source reports those bytes, and back-calculating them would present a guess as a measurement.
/// </summary>
public sealed partial class GpuMemoryViewModel : ObservableObject
{
    public static readonly TimeSpan ChartWindow = TimeSpan.FromMinutes(15);

    /// <summary>A Strata poll older than this is not used to link processes (the port may belong to someone else by then).</summary>
    public static readonly TimeSpan StrataMaxAge = TimeSpan.FromSeconds(15);

    private static readonly double ChartGapDays = TimeSpan.FromSeconds(25).TotalDays;
    private readonly GpuMemoryMonitor _monitor;
    private readonly SharedHardwareHub _hub;
    private readonly Func<StrataContext> _strata;
    private readonly TimeProvider _time;
    private readonly Dictionary<ProcessKey, int> _colorSlots = [];
    private bool _rendered;
    private DateTimeOffset? _renderedAt;
    private bool _renderedStale;
    private string? _renderedStrataKey;

    public GpuMemoryViewModel(GpuMemoryMonitor monitor, SharedHardwareHub hub, Func<StrataContext> strata, TimeProvider? time = null)
    {
        _monitor = monitor;
        _hub = hub;
        _strata = strata;
        _time = time ?? TimeProvider.System;
    }

    public ObservableCollection<string> Issues { get; } = [];
    public ObservableCollection<GpuAdapterCard> Adapters { get; } = [];
    public ObservableCollection<GpuProcessRow> Processes { get; } = [];
    public ObservableCollection<MemoryValueRow> StrataRows { get; } = [];
    public ObservableCollection<GpuProcessRow> StrataProcesses { get; } = [];
    public ObservableCollection<string> StrataEvidence { get; } = [];

    [ObservableProperty] public partial Level StatusLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string SourceText { get; set; } = "";
    [ObservableProperty] public partial string VariantText { get; set; } = "";
    [ObservableProperty] public partial bool IsMock { get; set; }
    [ObservableProperty] public partial bool HasValues { get; set; }
    [ObservableProperty] public partial string ProcessSummary { get; set; } = "";
    [ObservableProperty] public partial string ProcessEmptyText { get; set; } = "";
    [ObservableProperty] public partial Level StrataLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string StrataStatus { get; set; } = "";
    [ObservableProperty] public partial ChartModel? MemoryChart { get; set; }

    /// <summary>The page is on screen: the reader then reads every 2 s (otherwise every 10 s, for the history).</summary>
    [ObservableProperty] public partial bool IsVisible { get; set; }

    public IReadOnlyList<OriginLegendRow> Legend { get; } =
    [
        new("측정", "Windows가 그 어댑터·프로세스에 대해 보고한 값 (성능 카운터, DXGI, NVML)"),
        new("계산", "측정값의 합·차. 측정값의 한계를 그대로 가짐"),
        new("런타임 보고", "Strata가 자체 API로 보고한 값 (이 앱이 측정하지 않음)"),
        new("추정", "모델 크기·구조 가정으로 만든 값 — 이 화면에서는 쓰지 않음"),
        new("N/A", "측정할 수 없거나 보고되지 않음 (0이 아님)"),
    ];

    public IReadOnlyList<string> Explanations { get; } =
    [
        "전용(로컬): 프로세스가 그 GPU의 로컬 메모리(VRAM) 세그먼트에 가진 바이트입니다 (GPU Process Memory › Local Usage). 공유 리소스를 여러 프로세스에 중복 집계하는 Dedicated Usage는 Local Usage를 쓸 수 없을 때만 대신 읽고, 그때는 위에 경고가 나옵니다.",
        "공유(시스템 RAM): 프로세스가 GPU용으로 쓰는 시스템 RAM 세그먼트의 바이트입니다 (Non Local Usage). 고정(pinned) 버퍼처럼 의도적으로 할당한 메모리도 포함되므로 'VRAM이 모자라 RAM으로 밀려난 양(LLM 오프로딩)'과 같은 뜻이 아닙니다.",
        "커밋: 프로세스가 그 GPU에 할당한 전체 메모리입니다 (상주 여부와 무관, Total Committed). VRAM에서 RAM으로 옮겨진 데이터의 양은 Windows가 프로세스별로 보고하지 않으므로 이 화면은 그 값을 만들지 않습니다.",
        "프로세스 합계가 어댑터 사용량과 같다고 가정하지 않습니다. 차이는 커널·드라이버 할당, 읽지 못한 프로세스, 수집 시점 차이 등에서 생길 수 있고, 이 데이터만으로 원인을 나눌 수 없습니다.",
        "NVML(공통 센서)의 VRAM 사용량은 장치 전체 기준이라 Windows 어댑터 카운터와 집계 방식이 달라 값이 다를 수 있습니다. WDDM 드라이버에서 NVML은 프로세스별 메모리를 N/A로 보고하므로(NVIDIA NVML 문서), 프로세스별 값은 Windows 카운터에서만 읽습니다.",
        "LLM 메모리: 가중치·KV 캐시·추론 작업 버퍼·CUDA/드라이버 오버헤드로 나눈 값은 보여 주지 않습니다. Strata 텔레메트리에는 바이트 단위 내역이 없고, 프로세스 전체 VRAM에서 역산하면 추정을 측정처럼 보이게 하기 때문입니다.",
        "기록: 이 화면의 값은 메모리에만 최근 30분 보관하고 데이터베이스에 저장하지 않습니다(프로세스 이름 포함, 기존 DB 스키마 변경 없음). 장치 전체 VRAM은 지금처럼 각 모듈 DB에 기록됩니다.",
    ];

    public IReadOnlyList<string> Verification { get; } =
    [
        "작업 관리자 › 세부 정보 › 열 머리글 오른쪽 클릭 › 열 선택 › '전용 GPU 메모리'·'공유 GPU 메모리'를 켜고 같은 프로세스를 비교합니다. 작업 관리자 열이 어떤 카운터와 같은지는 확인하지 못했으므로, dwm.exe처럼 공유 리소스가 많은 프로세스는 값이 다를 수 있습니다.",
        "성능 모니터(perfmon) › 카운터 추가 › 'GPU Process Memory'(한국어 Windows에서는 한국어 표시 이름)의 Local Usage·Non Local Usage: 이 화면이 읽는 카운터 자체입니다. typeperf는 Windows 표시 언어의 카운터 이름을 씁니다.",
        "nvidia-smi --query-gpu=memory.used,memory.total --format=csv: 장치 전체 VRAM(NVML)과 'NVML 장치 VRAM' 행을 비교합니다. WDDM에서는 nvidia-smi도 프로세스별 메모리를 N/A로 표시합니다.",
        "알려진 크기의 부하로 확인: 모델을 로드하기 전·후에 strata.exe 행의 '전용' 변화와 nvidia-smi의 변화를 비교합니다.",
    ];

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

    /// <summary>Called every second while the page is visible (and once when it becomes visible). Cheap: the readings come from the reader thread.</summary>
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
        UpdateStatus(health, latest, age, stale);
        var strataKey = StrataKey(strata, now);
        // Rebuild the tables and the chart only when something they show changed (a new reading, freshness, Strata).
        if (_rendered && latest?.Timestamp == _renderedAt && stale == _renderedStale && strataKey == _renderedStrataKey)
        {
            return;
        }

        _rendered = true;
        _renderedAt = latest?.Timestamp;
        _renderedStale = stale;
        _renderedStrataKey = strataKey;
        IsMock = latest?.IsMock ?? health.IsMock;
        var current = latest is not null && !stale ? latest : null;
        HasValues = current is not null;
        Replace(Issues, IssuesOf(health, latest));
        SourceText = latest is null ? $"출처: {health.ProviderName}" : $"출처: {latest.Source}";
        VariantText = current is null ? "" : VariantDescription(current);
        var link = BuildStrata(strata, current, now);
        Replace(Adapters, current is null ? [] : BuildAdapters(current));
        BuildProcesses(current, link, stale, latest);
        MemoryChart = BuildChart(current, link);
    }

    /// <summary>A reading older than three intervals plus two seconds is not shown as current.</summary>
    public static TimeSpan StaleAfter(GpuMemorySnapshot snapshot)
        => (snapshot.ExpectedInterval > TimeSpan.Zero ? snapshot.ExpectedInterval : GpuMemoryMonitor.DefaultBackgroundInterval) * 3 + TimeSpan.FromSeconds(2);

    private void UpdateStatus(GpuMemoryHealth health, GpuMemorySnapshot? latest, TimeSpan? age, bool stale)
    {
        (StatusLevel, StatusText) = health.State switch
        {
            GpuMemoryMonitorState.Disabled => (Level.Muted, $"꺼짐 — {health.UnavailableReason}"),
            GpuMemoryMonitorState.Unavailable => (Level.Warning, $"사용 불가 — {health.UnavailableReason}"),
            _ when latest is null => (Level.Muted, health.State == GpuMemoryMonitorState.Running ? "첫 측정 기다리는 중" : "읽기 전"),
            _ when stale => (Level.Error,
                $"새 측정값이 없습니다 — 마지막 측정 {Text.Clock(latest.Timestamp)} ({Text.Age(age!.Value)}). 오래된 값은 현재 값처럼 표시하지 않습니다."),
            _ => (latest.IsMock || latest.Variant != ProcessCounterVariant.LocalNonLocal || latest.Issues.Count > 0 ? Level.Warning : Level.Good,
                $"측정 {Text.Clock(latest.Timestamp)} ({Text.Age(age!.Value)}) · 간격 {health.Interval.TotalSeconds:0.#}초"
                + $"{(health.IsActive ? "" : " (화면 밖)")} · 수집 {latest.CollectionTime.TotalMilliseconds:0} ms"
                + (health.Failures > 0 ? $" · 읽기 예외 {health.Failures}회" : "")),
        };
    }

    private static List<string> IssuesOf(GpuMemoryHealth health, GpuMemorySnapshot? latest)
    {
        var issues = (latest?.Issues ?? []).Select(i => $"{i.Source} [{i.Scope}]: {SensorStatusText.ToKorean(i.Status)} — {i.Message}").Distinct().ToList();
        if (health.LastError is { } error && health.Failures > 0)
        {
            issues.Add($"GPU 메모리 리더: 읽기 예외 {health.Failures}회 — 마지막: {error}");
        }

        return issues;
    }

    private static string VariantDescription(GpuMemorySnapshot s) => s.Variant switch
    {
        ProcessCounterVariant.LocalNonLocal => "프로세스 값: Local Usage(전용) · Non Local Usage(공유) — 각 프로세스에 청구된 메모리"
                                               + (s.CommittedAvailable ? " · Total Committed(커밋)" : " · 커밋 N/A"),
        ProcessCounterVariant.DedicatedShared => "⚠ 프로세스 값: Dedicated Usage · Shared Usage(대체 카운터) — 다른 프로세스와 공유된 리소스가 중복 집계될 수 있어 합계를 믿기 어렵습니다.",
        _ => "프로세스별 카운터를 읽을 수 없습니다 (N/A).",
    };

    private void UpdateWatchedPorts(StrataContext strata, DateTimeOffset now)
        => _monitor.SetWatchedPorts(StrataPort(strata, now) is int port ? [port] : []);

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

    private static string StrataKey(StrataContext strata, DateTimeOffset now)
        => strata.Snapshot is { } s ? $"{s.State}|{s.Endpoint}|{s.Model?.Id}|{s.Model?.ContextTokens}|{s.Slots.Sum(x => x.HeldTokens ?? 0)}|{StrataPort(strata, now)}" : strata.Unavailable ?? "-";

    private List<GpuAdapterCard> BuildAdapters(GpuMemorySnapshot s)
    {
        var nvidia = s.Adapters.Where(a => a.Info is { IsNvidia: true, IsSoftware: false }).ToList();
        var devices = _hub.GpuDevices;
        var hubSample = _hub.Latest?.Snapshot;
        var hubHealth = _hub.Health;
        var hubFresh = hubSample is not null && _time.GetUtcNow() - hubSample.Timestamp <= hubHealth.Interval * 2 + TimeSpan.FromSeconds(1);
        var cards = new List<GpuAdapterCard>();
        foreach (var adapter in s.Adapters)
        {
            var info = adapter.Info;
            var processes = s.Processes.Where(p => p.Luid == adapter.Luid).ToList();
            var rows = new List<MemoryValueRow>
            {
                new("전용 GPU 메모리 사용", Pair(adapter.DedicatedUsageBytes, info?.DedicatedVideoMemoryBytes), $"{Measured(s.IsMock)} · 용량은 DXGI 보고",
                    s.AdapterCountersAvailable ? "GPU Adapter Memory › Dedicated Usage" : "어댑터 카운터를 쓸 수 없어 N/A"),
                new("공유 GPU 메모리 사용", Pair(adapter.SharedUsageBytes, info?.SharedSystemMemoryBytes), $"{Measured(s.IsMock)} · 한도는 DXGI 보고",
                    "GPU가 쓰는 시스템 RAM (GPU Adapter Memory › Shared Usage). LLM 오프로딩 양과 같지 않음"),
            };
            if (info is { IsNvidia: true, IsSoftware: false })
            {
                if (nvidia.Count == 1 && devices.Count == 1)
                {
                    var gpu = hubFresh ? hubSample!.Gpu(devices[0].Index) : null;
                    rows.Add(new MemoryValueRow("NVML 장치 VRAM (공통 센서)",
                        gpu is null ? Text.NA : Pair(gpu.MemoryUsedBytes, gpu.MemoryTotalBytes),
                        gpu is null ? "N/A" : _hub.IsMock ? "MOCK (측정 아님)" : "측정 (NVML)",
                        gpu is null ? "공통 센서의 최근 측정값이 없습니다." : $"측정 {Text.Clock(hubSample!.Timestamp)} · 장치 전체 기준이라 Windows 카운터와 다를 수 있음"));
                }
                else
                {
                    var why = devices.Count == 0
                        ? $"NVML 장치 없음 ({_hub.GpuUnavailableReason ?? "공통 센서에서 GPU를 읽지 않음"})"
                        : "NVIDIA GPU가 여러 개라 PCI 위치 없이 NVML 장치와 Windows 어댑터를 짝짓지 않습니다";
                    rows.Add(new MemoryValueRow("NVML 장치 VRAM (공통 센서)", Text.NA, "N/A", why));
                }
            }

            var allDedicated = processes.Count > 0 && processes.All(p => p.DedicatedBytes is not null);
            long? sum = allDedicated ? processes.Sum(p => p.DedicatedBytes!.Value) : null;
            rows.Add(new MemoryValueRow("프로세스별 전용 합계", Text.Bytes(sum), "계산",
                sum is null ? "프로세스 카운터가 없거나 일부 프로세스 값이 N/A라 합계를 내지 않습니다."
                    : $"{processes.Count}개 프로세스의 전용 값 합. 어댑터 사용량과 같다고 가정하지 않음"
                      + (s.Variant == ProcessCounterVariant.DedicatedShared ? " (대체 카운터: 중복 집계 가능)" : "")));
            long? difference = sum is long total && adapter.DedicatedUsageBytes is long used ? used - total : null;
            rows.Add(new MemoryValueRow("어댑터 − 프로세스 합계", Text.SignedBytes(difference), "계산",
                difference is null ? "둘 중 하나가 N/A"
                    : difference < 0 ? "프로세스 합계가 더 큽니다: 같은 메모리가 여러 프로세스에 집계되었을 수 있습니다."
                    : "프로세스로 나뉘지 않은 양 (커널·드라이버 할당, 읽지 못한 프로세스, 수집 시점 차이 등 — 원인은 구분 불가)",
                difference < 0 ? Level.Warning : Level.Muted));
            var title = info is null ? $"어댑터 LUID {GpuLuid.Format(adapter.Luid)}" : info.Name;
            var subtitle = info is null
                ? "DXGI 목록에 없는 LUID (이름 확인 불가) · 카운터 값은 그대로 표시"
                : $"{info.VendorName} · LUID {info.LuidText}{(info.IsSoftware ? " · 소프트웨어 어댑터" : "")} · 프로세스 {processes.Count}개";
            string? note = info is { IsSoftware: false } && info.DedicatedVideoMemoryBytes <= 512L << 20
                ? "전용 메모리 용량이 작습니다(통합 GPU일 수 있음). 통합 GPU는 시스템 RAM을 GPU 메모리로 씁니다."
                : null;
            cards.Add(new GpuAdapterCard(title, subtitle, rows, note));
        }

        return cards;
    }

    private void BuildProcesses(GpuMemorySnapshot? current, ServerProcessLink? link, bool stale, GpuMemorySnapshot? latest)
    {
        if (current is null)
        {
            Replace(Processes, []);
            ProcessSummary = "";
            ProcessEmptyText = stale && latest is not null
                ? $"새 측정값이 없어 표시하지 않습니다 (마지막 측정 {Text.Clock(latest.Timestamp)})."
                : "측정값이 없습니다 (N/A).";
            return;
        }

        var linked = link?.Processes.Select(p => p.Process.ProcessId).ToHashSet() ?? [];
        var listed = current.Processes.Where(GpuMemoryRules.IsListed)
            .OrderByDescending(p => p.DedicatedBytes ?? -1).ThenByDescending(p => p.SharedBytes ?? -1).ThenBy(p => p.ProcessId)
            .ToList();
        Replace(Processes, listed.Select(p => Row(current, p, linked.Contains(p.ProcessId))).ToList());
        var hidden = current.Processes.Count - listed.Count;
        ProcessSummary = $"{listed.Count}개 표시 · 전용 1 MiB·공유/커밋 16 MiB 미만 {hidden}개 숨김 · "
                         + (current.IsMock ? "바이트 값은 모두 MOCK 가상 값 (측정 아님)" : "바이트 값은 모두 Windows 측정값");
        ProcessEmptyText = listed.Count == 0 ? "GPU 메모리를 쓰는 프로세스가 보고되지 않았습니다." : "";
    }

    private GpuProcessRow Row(GpuMemorySnapshot s, GpuProcessMemory p, bool isStrata)
    {
        var entry = s.Process(p.ProcessId);
        var adapter = s.Adapter(p.Luid)?.Info;
        var (identity, level) = entry?.State switch
        {
            ProcessIdentityState.Verified => ("확인됨", Level.Good),
            ProcessIdentityState.StartTimeUnavailable => ("시작 시각 확인 불가 (보호·다른 사용자 프로세스)", Level.Muted),
            ProcessIdentityState.Exited => ("측정 직후 종료됨", Level.Warning),
            ProcessIdentityState.ReusedAfterSample => ("PID 재사용됨 — 값은 이전 프로세스의 것", Level.Warning),
            _ => ("프로세스 정보 없음", Level.Warning),
        };
        return new GpuProcessRow(
            p.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry?.Name ?? $"PID {p.ProcessId}",
            adapter?.Name ?? GpuLuid.Format(p.Luid),
            Text.Bytes(p.DedicatedBytes),
            Text.Bytes(p.SharedBytes),
            s.CommittedAvailable ? Text.Bytes(p.CommittedBytes) : Text.NA,
            entry?.StartTime is { } start ? Text.DateTime(start) : Text.NA,
            identity,
            isStrata ? "Strata" : "",
            level);
    }

    private ServerProcessLink? BuildStrata(StrataContext strata, GpuMemorySnapshot? current, DateTimeOffset now)
    {
        var rows = new List<MemoryValueRow>();
        ServerProcessLink? link = null;
        if (strata.Snapshot is not { } rt)
        {
            StrataLevel = Level.Muted;
            StrataStatus = strata.Unavailable ?? "로컬 AI 모듈이 아직 Strata를 조회하지 않았습니다.";
            Replace(StrataRows, rows);
            Replace(StrataProcesses, []);
            Replace(StrataEvidence, []);
            return null;
        }

        var age = now - rt.Timestamp;
        var port = StrataPort(strata, now);
        StrataLevel = rt.IsReachable && port is not null ? Level.Good : rt.IsReachable ? Level.Warning : Level.Muted;
        StrataStatus = $"{RuntimeStateText.ToKorean(rt.State)} · {rt.Endpoint ?? Text.NA} · 조회 {Text.Clock(rt.Timestamp)} ({Text.Age(age)})"
                       + (rt.IsReachable && age > StrataMaxAge ? " — 조회가 오래되어 프로세스를 연결하지 않음" : "")
                       + (rt.Error is { } error ? $" · {error.Message}" : "");
        var model = rt.Model;
        rows.Add(new MemoryValueRow("모델 ID", model?.Id ?? Text.NA, "런타임 보고", model?.Loaded is bool loaded ? (loaded ? "로드됨" : "로드 안 됨") : ""));
        rows.Add(new MemoryValueRow("모델 파일", model?.ModelFileName ?? Text.NA, "런타임 보고",
            model?.ModelFileName is null ? "텔레메트리 키 권한 밖이거나 보고되지 않음 (파일 이름의 양자화 표기는 해석하지 않음)" : "파일 이름만 (경로 제외)"));
        rows.Add(new MemoryValueRow("양자화", Text.NA, "N/A", "Strata 텔레메트리가 보고하지 않습니다."));
        rows.Add(new MemoryValueRow("컨텍스트 창", model?.ContextTokens is long ctx ? $"{Text.Count(ctx)} 토큰" : Text.NA, "런타임 보고", "모델이 받을 수 있는 최대 토큰 수"));
        var held = rt.Slots.Where(slot => slot.HeldTokens is not null).ToList();
        rows.Add(new MemoryValueRow("슬롯이 가진 토큰", held.Count == 0 ? Text.NA
                : $"합계 {Text.Count(held.Sum(slot => slot.HeldTokens!.Value))} · 최대 {Text.Count(held.Max(slot => slot.HeldTokens!.Value))} (슬롯 {rt.Slots.Count}개)",
            "런타임 보고", "KV 캐시에 들어 있는 토큰 수. 바이트가 아니며 KV 캐시 메모리 크기로 바꾸지 않음"));
        if (port is not null && current is not null)
        {
            link = ServerProcessLinker.Link(rt.Endpoint, current);
        }

        if (link is { IsLinked: true })
        {
            var memory = link.Processes.SelectMany(p => p.Memory).ToList();
            long? dedicated = memory.Count > 0 && memory.All(m => m.DedicatedBytes is not null) ? memory.Sum(m => m.DedicatedBytes!.Value) : null;
            long? shared = memory.Count > 0 && memory.All(m => m.SharedBytes is not null) ? memory.Sum(m => m.SharedBytes!.Value) : null;
            var holders = link.Processes.Where(p => p.Memory.Any(m => m.DedicatedBytes > 0)).ToList();
            rows.Add(new MemoryValueRow("Strata 프로세스의 GPU 메모리",
                memory.Count == 0 ? "GPU 메모리를 쓰는 프로세스 없음" : $"전용 {Text.Bytes(dedicated)} · 공유 {Text.Bytes(shared)}",
                current!.IsMock ? "MOCK (측정 아님)" : holders.Count <= 1 && memory.Count <= 1 ? "측정 (Windows)" : "계산 (측정값의 합)",
                $"{string.Join(", ", link.Processes.Select(p => $"{p.Process.Name}({p.Process.ProcessId})"))} — 연결 근거는 아래", Level.Good));
            Replace(StrataProcesses, link.Processes.SelectMany(p => p.Memory.Count == 0
                    ? new[]
                    {
                        new GpuProcessRow(p.Process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Process.Name, "-", Text.NA, Text.NA,
                            Text.NA, p.Process.StartTime is { } s ? Text.DateTime(s) : Text.NA, Relation(p.Depth), "GPU 메모리 없음", Level.Muted),
                    }
                    : p.Memory.Select(m => Row(current!, m, true) with { Identity = Relation(p.Depth) }))
                .ToList());
            Replace(StrataEvidence, [.. link.Evidence, .. link.Unverified.Select(u => "연결 안 함: " + u)]);
        }
        else
        {
            var why = rt.IsReachable && port is null ? "Strata 주소가 이 PC가 아니거나 조회가 오래되어 프로세스와 연결하지 않습니다."
                : !rt.IsReachable ? "Strata에 연결되어 있지 않아 프로세스와 연결하지 않습니다."
                : current is null ? "GPU 메모리 측정값이 없어 연결하지 않습니다."
                : link?.Reason ?? "연결 정보를 기다리는 중입니다 (다음 측정에서 포트 소유 프로세스를 확인).";
            rows.Add(new MemoryValueRow("Strata 프로세스의 GPU 메모리", Text.NA, "N/A", why));
            Replace(StrataProcesses, []);
            Replace(StrataEvidence, []);
        }

        rows.Add(new MemoryValueRow("가중치 · KV 캐시 · 작업 버퍼", "알 수 없음", "N/A",
            "Strata는 이 내역을 바이트로 보고하지 않습니다. 프로세스 전체 VRAM에서 역산하지 않습니다."));
        Replace(StrataRows, rows);
        return link;
    }

    private ChartModel BuildChart(GpuMemorySnapshot? current, ServerProcessLink? link)
    {
        var history = _monitor.History(ChartWindow);
        var series = new List<ChartSeries>();
        if (current is not null && history.Count > 0)
        {
            var slot = 0;
            foreach (var adapter in current.Adapters.Where(a => a.Info is not { IsSoftware: true }).Take(2))
            {
                var points = history
                    .Select(h => (h.Timestamp, Value: h.Adapters.FirstOrDefault(a => a.Luid == adapter.Luid).DedicatedUsageBytes))
                    .Where(x => x.Value is not null)
                    .ToList();
                if (points.Count > 0)
                {
                    series.Add(new ChartSeries($"{Short(adapter.Info?.Name ?? GpuLuid.Format(adapter.Luid))} 전용 (어댑터)",
                        [.. points.Select(x => x.Timestamp.LocalDateTime.ToOADate())], [.. points.Select(x => Gib(x.Value!.Value))], ChartPalette.Slot(slot)));
                }

                slot++;
            }

            var linkedPids = link?.Processes.Select(p => p.Process.ProcessId).ToHashSet() ?? [];
            var keys = current.Processes
                .Where(p => p.DedicatedBytes is not null)
                .OrderByDescending(p => p.DedicatedBytes)
                .Select(p => (Process: p, Entry: current.Process(p.ProcessId)))
                .Where(x => x.Entry is not null)
                .Take(3)
                .Concat(current.Processes.Where(p => linkedPids.Contains(p.ProcessId) && p.DedicatedBytes > 0)
                    .Select(p => (Process: p, Entry: current.Process(p.ProcessId))))
                .DistinctBy(x => (x.Entry!.Key, x.Process.Luid))
                .ToList();
            foreach (var (process, entry) in keys)
            {
                var points = history
                    .Select(h => (h.Timestamp, Value: h.Processes.FirstOrDefault(x => x.Key == entry!.Key && x.Luid == process.Luid).DedicatedBytes))
                    .Where(x => x.Value is not null)
                    .ToList();
                if (points.Count == 0)
                {
                    continue;
                }

                series.Add(new ChartSeries($"{entry!.Name} ({entry.ProcessId})",
                    [.. points.Select(x => x.Timestamp.LocalDateTime.ToOADate())], [.. points.Select(x => Gib(x.Value!.Value))],
                    ChartPalette.Slot(ColorSlot(entry.Key))));
            }
        }

        return new ChartModel
        {
            Title = "GPU 전용 메모리 변화 (최근 15분, 측정값)",
            Unit = "GiB",
            Series = series,
            XIsTime = true,
            GapThreshold = ChartGapDays,
            YMin = 0,
            ValueFormat = "0.00",
            EmptyText = "표시할 측정값이 없습니다 (N/A)",
        };
    }

    /// <summary>A process keeps its color while it is on the chart (colors follow the process, not its rank).</summary>
    private int ColorSlot(ProcessKey key)
    {
        if (!_colorSlots.TryGetValue(key, out var slot))
        {
            slot = 2 + (_colorSlots.Count % (ChartPalette.Categorical.Length - 2));
            _colorSlots[key] = slot;
        }

        return slot;
    }

    /// <summary>The origin tag of a Windows value: never "측정" for MOCK readings.</summary>
    private static string Measured(bool isMock) => isMock ? "MOCK (측정 아님)" : "측정";

    private static string Relation(int depth) => depth == 0 ? "포트 소유 프로세스" : $"하위 프로세스 ({depth}단계)";

    private static string Pair(long? used, long? total) => used is null && total is null ? Text.NA : $"{Text.Bytes(used)} / {Text.Bytes(total)}";

    private static double Gib(long bytes) => bytes / (1024.0 * 1024 * 1024);

    private static string Short(string name) => name.Length <= 28 ? name : name[..27] + "…";

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
