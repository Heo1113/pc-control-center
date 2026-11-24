using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.Core;
using PcControlCenter.Hardware;
using PcControlCenter.Modules;

namespace PcControlCenter.Presentation;

public sealed record SensorSourceRow(string Metric, string Name, string Source, string Status, string Elevation, string Detail, Level Level);

public sealed record NameValue(string Name, string Value);

/// <summary>The one sensor reader: health, who consumes it, sensor support (A's capability table) and device facts.</summary>
public sealed partial class SharedSensorsViewModel(SharedHardwareHub hub, SensorIntervalCoordinator intervals, TimeProvider? time = null) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool IsMock => hub.IsMock;

    public ObservableCollection<NameValue> Health { get; } = [];
    public ObservableCollection<NameValue> Consumers { get; } = [];
    public ObservableCollection<SensorSourceRow> Sources { get; } = [];
    public ObservableCollection<NameValue> Device { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];

    [ObservableProperty] public partial Level HealthLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string HealthText { get; set; } = "";

    public string Explanation =>
        "PC Control Center는 GPU(NVML)와 CPU·RAM 센서를 이 리더 하나로만 읽습니다. 로컬 AI(A)·GPU 경제성(B)·시스템 진단(C)은 각자의 간격으로 "
        + "이 측정값을 받아 자기 데이터베이스에 원래 측정 시각 그대로 기록합니다. 화면용으로 줄이거나 평균한 값은 기록·비용 계산에 쓰지 않습니다. "
        + "측정되지 않은 구간은 각 모듈이 공백으로 기록하며 값을 채워 넣지 않습니다.";

    public void Refresh()
    {
        var h = hub.Health;
        var now = _time.GetUtcNow();
        var age = h.LastReadingAt is { } at ? now - at : (TimeSpan?)null;
        HealthLevel = h.State != HubState.Running ? Level.Error
            : age is null ? Level.Muted
            : age > h.Interval * 2 + TimeSpan.FromSeconds(1) ? Level.Error
            : h.IsMock || h.LatestIssues.Count > 0 ? Level.Warning : Level.Good;
        HealthText = h.State switch
        {
            HubState.Running when age is null => "첫 측정 기다리는 중",
            HubState.Running => $"동작 중 · 마지막 측정 {Text.Clock(h.LastReadingAt)} ({Text.Age(age!.Value)})",
            HubState.Stopped => "중지됨",
            _ => "시작 전",
        };
        Replace(Health,
        [
            new NameValue("상태", HealthText),
            new NameValue("측정 간격", $"{h.Interval.TotalMilliseconds:0} ms (실행 중인 모듈이 요청한 가장 짧은 간격, 최대 {intervals.MaxInterval.TotalMilliseconds:0} ms)"),
            new NameValue("측정 횟수", Text.Count(h.Readings)),
            new NameValue("건너뛴 측정", $"{Text.Count(h.SkippedTicks)}회 (측정이 간격보다 오래 걸린 경우, 값을 채우지 않음)"),
            new NameValue("읽기 중 예외", Text.Count(h.LoopErrors) + (h.LastError is null ? "" : $" — {h.LastError}")),
            new NameValue("데이터 종류", h.IsMock ? "MOCK 가상 센서 (실제 측정 아님)" : "실측"),
        ]);
        var requests = intervals.Requests;
        Replace(Consumers, ModuleIds.All.Where(id => id != ModuleIds.Operator).Select(id => new NameValue(
            $"{ModuleIds.DisplayName(id)} ({ModuleIds.Origin(id)[..1]})",
            requests.TryGetValue(id, out var interval) ? $"{interval.TotalSeconds:0.#}초마다 기록 (공유 측정값 사용)" : "센서를 사용하지 않음 (모듈 꺼짐·중지)")).ToList());
        Replace(Sources, hub.Capabilities.Select(c => new SensorSourceRow(c.MetricKey, c.DisplayName, c.Source, SensorStatusText.ToKorean(c.Status),
            c.RequiresElevation ? "관리자 권한 필요 (수집 안 함)" : "필요 없음", c.Detail ?? "",
            c.Status == SensorStatus.Ok ? Level.Good : c.Status is SensorStatus.RequiresElevation or SensorStatus.NotImplemented or SensorStatus.Disabled ? Level.Muted : Level.Warning)).ToList());
        var info = hub.Info;
        Replace(Device,
        [
            new NameValue("CPU", $"{info.CpuName ?? Text.NA} · 논리 프로세서 {info.LogicalProcessors}"),
            new NameValue("RAM", Text.Gib(info.RamTotalBytes)),
            new NameValue("운영체제", info.OsDescription),
            new NameValue("GPU 드라이버 / NVML", $"{info.GpuDriverVersion ?? Text.NA} / {info.NvmlVersion ?? Text.NA}"),
            new NameValue("GPU", info.Gpus.Count == 0 ? $"없음 또는 읽을 수 없음 ({hub.GpuUnavailableReason ?? Text.NA})"
                : string.Join(", ", info.Gpus.Select(g => $"#{g.Index} {g.Name} ({Text.Gib(g.MemoryTotalBytes)}, 전력 제한 {Text.Num(g.PowerLimitW, "0", "W")})"))),
            new NameValue("관리자 권한", info.IsElevated ? "예 (필요하지 않습니다)" : "아니요 (정상)"),
        ]);
        var issues = h.LatestIssues.Select(i => $"{i.Source} [{i.Scope}]: {SensorStatusText.ToKorean(i.Status)} — {i.Message}").ToList();
        if (!issues.SequenceEqual(Issues))
        {
            Replace(Issues, issues);
        }
    }

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
