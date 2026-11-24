using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Recording;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>AI runtime (Strata) connection, identity, settings, live inference numbers, requests and errors.</summary>
public sealed partial class RuntimeViewModel(MonitoringService service) : PageViewModel
{
    private const string Reported = "엔진 보고";
    private const string Derived = "계산값";

    public override string Title => "AI 런타임";
    public override string Glyph => "";

    [ObservableProperty] public partial string Endpoint { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string StateText { get; set; } = "-";
    [ObservableProperty] public partial StatusLevel StateLevel { get; set; }
    [ObservableProperty] public partial string ErrorCategory { get; set; } = "";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial string ErrorHint { get; set; } = "";
    [ObservableProperty] public partial string LastSuccess { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string NextRetry { get; set; } = "";
    [ObservableProperty] public partial string KeyStatus { get; set; } = "";
    [ObservableProperty] public partial ChartModel? ThroughputChart { get; set; }

    public ObservableCollection<ValueRow> Server { get; } = [];
    public ObservableCollection<ValueRow> Model { get; } = [];
    public ObservableCollection<ValueRow> Settings { get; } = [];
    public ObservableCollection<ValueRow> Live { get; } = [];
    public ObservableCollection<SlotRow> Slots { get; } = [];
    public ObservableCollection<RequestDisplayRow> Recent { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];

    public override Task OnActivatedAsync()
    {
        Update();
        return Task.CompletedTask;
    }

    public override void OnFrame(TelemetryFrame frame)
    {
        Update();
        var frames = DashboardViewModel.Window(service.Live.Snapshot(), frame.Timestamp, TimeSpan.FromMinutes(Math.Clamp(service.Settings.Ui.LiveChartMinutes, 1, 60)));
        ThroughputChart = ChartBuilder.Throughput(frames, service.Interval);
    }

    [RelayCommand]
    private void Reconnect()
    {
        service.ReconnectRuntime();
        NextRetry = "다음 조회 때 바로 다시 연결합니다.";
    }

    private void Update()
    {
        var adapter = service.Runtime;
        var keyVariable = service.Settings.Strata.ApiKeyEnvironmentVariable;
        KeyStatus = string.IsNullOrWhiteSpace(keyVariable)
            ? "API 키 환경 변수: 사용 안 함"
            : $"API 키 환경 변수 {keyVariable}: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(keyVariable)) ? "값 없음" : "값 있음 (내용은 표시하지 않음)")}";
        if (adapter is null)
        {
            Endpoint = Fmt.NA;
            StateText = "꺼짐 - 설정에서 Strata 연결을 켤 수 있습니다";
            StateLevel = StatusLevel.Neutral;
            return;
        }

        Endpoint = adapter is Runtimes.Strata.StrataAdapter { ConfiguredEndpoint: string configured }
            ? $"{adapter.EndpointDisplay} (자동으로 찾음, 설정 주소 {configured})"
            : adapter.EndpointDisplay;
        var rt = service.LatestRuntime;
        if (rt is null)
        {
            StateText = "연결 중";
            StateLevel = StatusLevel.Neutral;
            return;
        }

        StateText = RuntimeStateText.ToKorean(rt.State);
        StateLevel = rt.State switch
        {
            RuntimeConnectionState.Connected => StatusLevel.Good,
            RuntimeConnectionState.Limited or RuntimeConnectionState.Timeout or RuntimeConnectionState.AuthRequired => StatusLevel.Warning,
            RuntimeConnectionState.Unreachable => StatusLevel.Neutral,
            _ => StatusLevel.Critical,
        };
        ErrorCategory = rt.Error is { } e ? RuntimeStateText.ToKorean(e.Category) : "";
        ErrorMessage = rt.Error?.Message ?? "";
        ErrorHint = rt.Error?.Hint ?? "";
        LastSuccess = Fmt.DateTime(rt.LastSuccess);
        NextRetry = rt.NextRetryAt is DateTimeOffset next ? $"다음 재시도: {Fmt.Time(next)}" : "";

        Server.Clear();
        Server.Add(new ValueRow("서비스", rt.Server?.Service ?? Fmt.NA, Reported));
        var restricted = rt.Server?.MetadataRestricted == true;
        // A read-only telemetry key cannot read /v1/status, /props or /v1/models: those rows are left out, not shown as N/A.
        if (!restricted || rt.Server?.EngineVersion is not null)
        {
            Server.Add(new ValueRow("엔진 버전", rt.Server?.EngineVersion ?? Fmt.NA, Reported, rt.Server?.EngineVersionNote ?? ""));
        }
        Server.Add(new ValueRow("텔레메트리 출처", rt.Server?.TelemetrySource switch
        {
            "server-v1" => "/v1/telemetry (schema v1)",
            "legacy-adapter" => "구버전 /metrics + /v1/status",
            "status-only" => "/v1/status만 (실시간 값 없음)",
            null => Fmt.NA,
            var other => other,
        }));
        Server.Add(new ValueRow("서버 인스턴스", rt.Server?.InstanceId is string id ? id[..Math.Min(12, id.Length)] + "…" : Fmt.NA, Reported,
            rt.Server?.InstanceState is string s ? $"상태 {s}" : ""));
        Server.Add(new ValueRow("서버 시작", Fmt.Unix(rt.Server?.BootTimeUnix), Reported));
        Server.Add(new ValueRow("서버 API 키 설정", Fmt.Bool(rt.Server?.ApiKeyConfigured), Reported));

        Model.Clear();
        Model.Add(new ValueRow("모델", rt.Model?.Id ?? Fmt.NA, Reported, rt.IsReachable || rt.Model?.Id is null ? "" : "마지막으로 확인된 값"));
        Model.Add(new ValueRow("로드 상태", Fmt.Bool(rt.Model?.Loaded), Reported));
        if (!restricted || rt.Model?.ModelFileName is not null)
        {
            Model.Add(new ValueRow("모델 파일", rt.Model?.ModelFileName ?? Fmt.NA, Reported, "파일 이름만 표시 (경로 제외)"));
        }
        Model.Add(new ValueRow("컨텍스트 한도", Fmt.Int(rt.Model?.ContextTokens, "tokens"), Reported));
        Model.Add(new ValueRow("병렬 슬롯", Fmt.Int(rt.Work?.SlotsEffective), Reported,
            rt.Work?.SlotsRequested is int requested ? $"요청값 {requested}" : ""));
        Model.Add(new ValueRow("비전 입력", Fmt.Bool(rt.Model?.Vision), Reported));
        if (!restricted || rt.Model?.Aliases.Count > 0)
        {
            Model.Add(new ValueRow("별칭", rt.Model?.Aliases.Count > 0 ? string.Join(", ", rt.Model.Aliases) : Fmt.NA, Reported));
        }

        Settings.Clear();
        foreach (var setting in rt.Settings)
        {
            Settings.Add(new ValueRow(setting.Key, setting.Value, Reported, "/props 기본 생성 설정"));
        }



        Live.Clear();
        var reachable = rt.IsReachable;
        var last = reachable ? rt.LastCompletedRequest : null;
        if (reachable && rt.Rates?.AnswerTokPerSec is double answer)
        {
            Live.Add(new ValueRow("답변 생성 속도", Fmt.Num(answer, "0.0", "tok/s"), Reported,
                rt.Rates?.AnswerWindowSeconds is double window ? $"생성 중 · 최근 {window:0.#}초 구간" : "생성 중"));
        }
        else
        {
            Live.Add(new ValueRow("답변 생성 속도", last?.DecodeTokPerSec is double lastDecode ? Fmt.Num(lastDecode, "0.0", "tok/s") : Fmt.NA,
                last?.DecodeTokPerSec is null ? Reported : Derived,
                last?.DecodeTokPerSec is null ? "생성 중일 때 표시 (완료된 요청 기록 없음)" : $"지금은 생성 중 아님 · 마지막 완료 요청 {Fmt.Unix(last.StartedUnix)}: 출력 토큰 ÷ 디코드 시간"));
        }

        if (reachable && rt.Rates?.PrefillTokPerSec is double prefill)
        {
            Live.Add(new ValueRow("프롬프트 처리 속도", Fmt.Num(prefill, "0.0", "tok/s"), Reported));
        }
        else
        {
            Live.Add(new ValueRow("프롬프트 처리 속도", last?.PrefillTokPerSec is double lastPrefill ? Fmt.Num(lastPrefill, "0.0", "tok/s") : Fmt.NA,
                last?.PrefillTokPerSec is null ? Reported : Derived,
                last?.PrefillTokPerSec is null ? "" : "마지막 완료 요청: (프롬프트 − 캐시 재사용) 토큰 ÷ 프롬프트 처리 시간"));
        }
        Live.Add(new ValueRow("전체 출력 처리량", reachable ? Fmt.Num(rt.Rates?.AggregateOutputTokPerSec, "0.0", "tok/s") : Fmt.NA, Derived,
            "출력 토큰 카운터 증가분 ÷ 조회 간격"));
        Live.Add(new ValueRow("처리 중 / 대기 / 대기열", reachable && rt.Work is { } w
            ? $"{Fmt.Int(w.Active)} / {Fmt.Int(w.Waiting)} / {Fmt.Int(w.Queued)}" : Fmt.NA, Reported));
        Live.Add(new ValueRow("가장 오래 기다린 요청", reachable ? Fmt.Num(rt.Work?.OldestQueueSeconds, "0.0", "s") : Fmt.NA, Reported));
        Live.Add(new ValueRow("입장 거부 / 대기 시간 초과 / 연결 끊김", reachable && rt.Work is { } c
            ? $"{Fmt.Int(c.AdmissionRejected)} / {Fmt.Int(c.QueueTimeouts)} / {Fmt.Int(c.ClientDisconnectCancels)}" : Fmt.NA, Reported, "서버 시작 이후 누계"));
        Live.Add(new ValueRow("누적 요청 / 출력 토큰", reachable && rt.Totals is { } t
            ? $"{Fmt.Int(t.Requests)} / {Fmt.Int(t.OutputTokens)}" : Fmt.NA, Reported, "서버 시작 이후"));

        Slots.Clear();
        if (reachable)
        {
            foreach (var slot in rt.Slots)
            {
                var fill = slot.HeldTokens is long held && rt.Model?.ContextTokens is long ctx && ctx > 0 ? Fmt.Percent(100.0 * held / ctx) : Fmt.NA;
                Slots.Add(new SlotRow(slot.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture), slot.State ?? Fmt.NA,
                    Fmt.Int(slot.HeldTokens), fill, Fmt.Num(slot.TokPerSec, "0.0")));
            }
        }

        Recent.Clear();
        foreach (var r in rt.RecentRequests.Take(20))
        {
            Recent.Add(new RequestDisplayRow(Fmt.Unix(r.StartedUnix), Fmt.Num(r.DurationSeconds, "0.0", "s"), r.Finish ?? Fmt.NA,
                Fmt.Int(r.PromptTokens), Fmt.Int(r.ReusedTokens), Fmt.Int(r.OutputTokens), Fmt.Num(r.PromptMs, "0"), Fmt.Num(r.DecodeMs, "0"),
                Fmt.Num(r.DecodeTokPerSec, "0.0"), Fmt.Num(r.FirstEngineTokenSeconds, "0.00", "s"), r.IsError));
        }

        Activity.Clear();
        foreach (var a in rt.Activity.Reverse().Take(32))
        {
            Activity.Add(new ActivityRow(Fmt.Unix(a.AtUnix), a.RequestId, a.Event, a.Outcome ?? ""));
        }
    }
}
