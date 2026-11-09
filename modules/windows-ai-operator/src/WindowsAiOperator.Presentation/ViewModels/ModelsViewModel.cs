using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Evaluation;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.ModelProviders;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation.ViewModels;

public sealed partial class SelectableProfile(ModelProfile profile) : ObservableObject
{
    public ModelProfile Profile { get; } = profile;
    public string Name => Profile.Name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class SelectableScenario(EvaluationScenario scenario) : ObservableObject
{
    public EvaluationScenario Scenario { get; } = scenario;
    public string Name => $"{Scenario.Name} — \"{Scenario.Request}\"";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}

public sealed record EvalSummaryRow(string Profile, string Model, bool IsMock, int Runs, int PlanValid, int GuiSuccess, double AvgSteps, double AvgSeconds, int ModelCalls, int ModelErrors, int WrongTargets, int Interventions)
{
    public string SuccessRate => Runs == 0 ? "-" : $"{GuiSuccess * 100.0 / Runs:0}% ({GuiSuccess}/{Runs})";
    public string PlanRate => Runs == 0 ? "-" : $"{PlanValid * 100.0 / Runs:0}% ({PlanValid}/{Runs})";
    public string Label => IsMock ? $"{Profile} (MOCK)" : Profile;
}

/// <summary>
/// Model connections: Strata / OpenAI-compatible servers configured by the user (address, key source, model chosen
/// from the server's list), connection and capability checks, the image test, the answer-format test and model comparison.
/// </summary>
public sealed partial class ModelsViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _evaluationCts;

    public ModelsViewModel(OperatorContext context, IDialogService dialogs, IUiDispatcher ui)
        : base("모델", "", "models")
    {
        _context = context;
        _dialogs = dialogs;
        _ui = ui;
        Kinds = [new("Strata", ProviderKind.Strata), new("OpenAI 호환", ProviderKind.OpenAiCompatible), new("MOCK(테스트용)", ProviderKind.Mock)];
        KeySources = [new("키 없음", ApiKeySource.None), new("환경 변수", ApiKeySource.EnvironmentVariable), new("암호화 저장(DPAPI)", ApiKeySource.StoredEncrypted)];
        FormatModes = [new("자동(JSON 스키마 → 폴백)", StructuredOutputMode.Auto), new("JSON 스키마", StructuredOutputMode.JsonSchema), new("JSON 객체", StructuredOutputMode.JsonObject), new("프롬프트만", StructuredOutputMode.PromptOnly)];
        VisionModes = [new("지원될 때만 사용", VisionPreference.UseWhenSupported), new("사용 안 함", VisionPreference.Disabled)];
        foreach (var scenario in ModelEvaluator.DefaultScenarios)
        {
            Scenarios.Add(new SelectableScenario(scenario));
        }

        ReloadProfiles();
        LoadEvaluations();
    }

    public IReadOnlyList<Option<ProviderKind>> Kinds { get; }
    public IReadOnlyList<Option<ApiKeySource>> KeySources { get; }
    public IReadOnlyList<Option<StructuredOutputMode>> FormatModes { get; }
    public IReadOnlyList<Option<VisionPreference>> VisionModes { get; }
    public ObservableCollection<ModelProfile> Profiles { get; } = [];
    public ObservableCollection<string> AvailableModels { get; } = [];
    public ObservableCollection<SelectableProfile> CompareProfiles { get; } = [];
    public ObservableCollection<SelectableScenario> Scenarios { get; } = [];
    public ObservableCollection<EvalRunRecord> EvalRuns { get; } = [];
    public ObservableCollection<EvalSummaryRow> EvalSummary { get; } = [];

    [ObservableProperty]
    public partial ModelProfile? Selected { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Option<ProviderKind>? EditKind { get; set; }

    [ObservableProperty]
    public partial string EditBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? EditModel { get; set; }

    [ObservableProperty]
    public partial Option<ApiKeySource>? EditKeySource { get; set; }

    [ObservableProperty]
    public partial string? EditKeyVariable { get; set; }

    [ObservableProperty]
    public partial int EditTimeout { get; set; }

    [ObservableProperty]
    public partial int EditMaxTokens { get; set; }

    [ObservableProperty]
    public partial double EditTemperature { get; set; }

    [ObservableProperty]
    public partial Option<StructuredOutputMode>? EditFormat { get; set; }

    [ObservableProperty]
    public partial Option<VisionPreference>? EditVision { get; set; }

    [ObservableProperty]
    public partial bool EditAllowRemote { get; set; }

    [ObservableProperty]
    public partial bool EditAllowRemoteScreenshots { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = "아직 확인하지 않았습니다.";

    [ObservableProperty]
    public partial Tone ConnectionTone { get; set; }

    [ObservableProperty]
    public partial string CapabilityText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VisionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Tone VisionTone { get; set; }

    [ObservableProperty]
    public partial string? TestResult { get; set; }

    [ObservableProperty]
    public partial string ScopeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasStoredKey { get; set; }

    [ObservableProperty]
    public partial bool UseRealTestApp { get; set; }

    [ObservableProperty]
    public partial string? EvaluationProgress { get; set; }

    [ObservableProperty]
    public partial bool IsEvaluating { get; set; }

    public bool SecretStoreAvailable => _context.Secrets.IsAvailable;
    public bool CanUseRealTestApp => !_context.Desktop.IsMock;

    public override void OnNavigatedTo()
    {
        ReloadProfiles();
        LoadEvaluations();
    }

    private void ReloadProfiles()
    {
        var id = Selected?.Id ?? _context.Settings.ActiveModelProfileId;
        Profiles.Clear();
        CompareProfiles.Clear();
        foreach (var profile in _context.Settings.ModelProfiles)
        {
            Profiles.Add(profile);
            CompareProfiles.Add(new SelectableProfile(profile) { IsSelected = profile.Id == _context.Settings.ActiveModelProfileId });
        }

        Selected = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedChanged(ModelProfile? value)
    {
        if (value is null)
        {
            return;
        }

        EditName = value.Name;
        EditKind = Kinds.First(k => k.Value == value.Kind);
        EditBaseUrl = value.BaseUrl;
        EditModel = value.Model;
        EditKeySource = KeySources.First(k => k.Value == value.KeySource);
        EditKeyVariable = value.KeyEnvironmentVariable;
        EditTimeout = value.TimeoutSeconds;
        EditMaxTokens = value.MaxTokens;
        EditTemperature = value.Temperature;
        EditFormat = FormatModes.First(f => f.Value == value.StructuredOutput);
        EditVision = VisionModes.First(v => v.Value == value.Vision);
        EditAllowRemote = value.AllowRemote;
        EditAllowRemoteScreenshots = value.AllowRemoteScreenshots;
        IsActive = value.Id == _context.Settings.ActiveModelProfileId;
        HasStoredKey = _context.Secrets.IsAvailable && _context.Secrets.Get(ApiKeyResolver.SecretKeyFor(value)) is not null;
        AvailableModels.Clear();
        if (value.Model is not null)
        {
            AvailableModels.Add(value.Model);
        }

        TestResult = null;
        ShowProbe(_context.Models.LastProbe(value));
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is null)
        {
            return;
        }

        var modelChanged = !string.Equals(Selected.Model, EditModel, StringComparison.Ordinal);
        Selected.Name = string.IsNullOrWhiteSpace(EditName) ? Selected.Name : EditName.Trim();
        Selected.Kind = EditKind?.Value ?? Selected.Kind;
        Selected.BaseUrl = EditBaseUrl.Trim();
        Selected.Model = string.IsNullOrWhiteSpace(EditModel) ? null : EditModel.Trim();
        Selected.KeySource = EditKeySource?.Value ?? ApiKeySource.None;
        Selected.KeyEnvironmentVariable = string.IsNullOrWhiteSpace(EditKeyVariable) ? null : EditKeyVariable.Trim();
        Selected.TimeoutSeconds = EditTimeout;
        Selected.MaxTokens = EditMaxTokens;
        Selected.Temperature = EditTemperature;
        Selected.StructuredOutput = EditFormat?.Value ?? StructuredOutputMode.Auto;
        Selected.Vision = EditVision?.Value ?? VisionPreference.UseWhenSupported;
        Selected.AllowRemote = EditAllowRemote;
        Selected.AllowRemoteScreenshots = EditAllowRemote && EditAllowRemoteScreenshots;
        if (modelChanged)
        {
            Selected.VisionVerified = false;
            Selected.VisionVerifiedModel = null;
        }

        var notes = _context.SaveSettings();
        var endpoint = EndpointPolicy.Check(Selected);
        TestResult = notes.Count > 0 ? "저장했습니다. " + string.Join(" ", notes) : endpoint.Problem is null ? "저장했습니다." : "저장했습니다. 주의: " + endpoint.Problem;
        ShowProbe(_context.Models.LastProbe(Selected));
        var id = Selected.Id;
        ReloadProfiles();
        Selected = Profiles.FirstOrDefault(p => p.Id == id);
    }

    [RelayCommand]
    private void SetActive()
    {
        if (Selected is null)
        {
            return;
        }

        _context.Settings.ActiveModelProfileId = Selected.Id;
        _context.SaveSettings();
        IsActive = true;
        ReloadProfiles();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ModelProfile { Name = "새 연결", Kind = ProviderKind.OpenAiCompatible, BaseUrl = "http://127.0.0.1:8080/v1" };
        _context.Settings.ModelProfiles.Add(profile);
        _context.SaveSettings();
        ReloadProfiles();
        Selected = Profiles.FirstOrDefault(p => p.Id == profile.Id);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (Selected is null || _context.Settings.ModelProfiles.Count <= 1)
        {
            return;
        }

        if (!_dialogs.Confirm("연결 삭제", $"'{Selected.Name}' 연결 설정을 삭제합니다. 저장된 API 키도 함께 지웁니다. 계속할까요?"))
        {
            return;
        }

        if (_context.Secrets.IsAvailable)
        {
            _context.Secrets.Set(ApiKeyResolver.SecretKeyFor(Selected), null);
        }

        _context.Settings.ModelProfiles.Remove(Selected);
        _context.SaveSettings();
        Selected = null;
        ReloadProfiles();
    }

    /// <summary>Called by the view with the content of a password box; the value is encrypted and never kept here.</summary>
    public void StoreApiKey(string? key)
    {
        if (Selected is null || !_context.Secrets.IsAvailable)
        {
            return;
        }

        _context.Secrets.Set(ApiKeyResolver.SecretKeyFor(Selected), string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        HasStoredKey = !string.IsNullOrWhiteSpace(key);
        if (HasStoredKey)
        {
            Selected.KeySource = ApiKeySource.StoredEncrypted;
            EditKeySource = KeySources.First(k => k.Value == ApiKeySource.StoredEncrypted);
            _context.SaveSettings();
        }

        TestResult = HasStoredKey ? "API 키를 이 PC의 사용자 계정으로 암호화해 저장했습니다(설정 파일·로그에 남지 않음)." : "저장된 API 키를 지웠습니다.";
    }

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (Selected is null)
        {
            return;
        }

        Save();
        IsBusy = true;
        ConnectionText = "연결을 확인하는 중…";
        ConnectionTone = Tone.Info;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await _context.Models.ProbeAsync(Selected, timeout.Token).ConfigureAwait(true);
            AvailableModels.Clear();
            foreach (var model in result.Models)
            {
                AvailableModels.Add(model.Id);
            }

            if (Selected.Model is null && result.Models.Count > 0)
            {
                EditModel = result.Models.FirstOrDefault(m => m.Note is null)?.Id ?? result.Models[0].Id;
                Save();
            }
            else if (Selected.Model is not null && !AvailableModels.Contains(Selected.Model))
            {
                AvailableModels.Insert(0, Selected.Model);
            }

            ShowProbe(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestVisionAsync()
    {
        if (Selected is null)
        {
            return;
        }

        Save();
        IsBusy = true;
        TestResult = "합성 이미지 2장(빨강·파랑 사각형, 화면 내용 아님)을 보내 이미지 입력을 확인하는 중…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Selected.TimeoutSeconds * 2 + 10));
            var result = await _context.Models.TestVisionAsync(Selected, timeout.Token).ConfigureAwait(true);
            TestResult = $"이미지 입력 테스트: {(result.Passed ? "통과" : "실패")} — {result.Message} ({result.Elapsed.TotalSeconds:0.0}초)";
            ShowProbe(_context.Models.LastProbe(Selected));
        }
        catch (OperationCanceledException)
        {
            TestResult = "이미지 입력 테스트 시간이 초과되었습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestFormatAsync()
    {
        if (Selected is null)
        {
            return;
        }

        Save();
        IsBusy = true;
        TestResult = "가상 테스트 앱 화면으로 실제 응답 형식을 확인하는 중(아무것도 조작하지 않음)…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Selected.TimeoutSeconds * 3 + 10));
            var result = await _context.Models.TestPlanFormatAsync(Selected, timeout.Token).ConfigureAwait(true);
            TestResult = $"응답 형식 검사: {(result.Valid ? "유효" : "거부")} — {result.Message} (모델 호출 {result.ModelCalls}회, {result.Elapsed.TotalSeconds:0.0}초)" +
                         (result.Plan is null ? string.Empty : "\n" + string.Join("\n", result.Plan.Steps.Select((s, i) => $"  {i + 1}. {s.Summary()}")));
        }
        catch (OperationCanceledException)
        {
            TestResult = "응답 형식 검사 시간이 초과되었습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowProbe(ProbeResult? result)
    {
        if (Selected is null)
        {
            return;
        }

        var endpoint = EndpointPolicy.Check(Selected);
        ScopeText = Selected.Kind == ProviderKind.Mock
            ? "MOCK: 네트워크를 쓰지 않는 테스트용 가짜 모델입니다(실제 AI 아님)."
            : endpoint.Scope switch
            {
                EndpointScope.Loopback => "이 PC의 서버 — 화면 정보가 PC 밖으로 나가지 않습니다.",
                EndpointScope.LocalNetwork => "같은 네트워크의 다른 PC — 화면 텍스트가 네트워크로 전송됩니다." + (Selected.AllowRemote ? " (허용됨)" : " (허용 필요)"),
                _ => "외부(클라우드) 서버 — 화면 텍스트가 외부로 전송됩니다." + (Selected.AllowRemote ? " (명시적으로 허용됨)" : " (허용 필요)"),
            };
        if (result is null)
        {
            ConnectionText = endpoint.Problem ?? "아직 확인하지 않았습니다. [연결 확인]을 누르세요.";
            ConnectionTone = endpoint.Problem is null ? Tone.Neutral : Tone.Warning;
            CapabilityText = string.Empty;
        }
        else
        {
            ConnectionText = result.Reachable
                ? $"{result.Message} · 서버: {result.ServerKind}{(result.ServerModel is null ? string.Empty : $" · 로드된 모델 {result.ServerModel}")} · {result.Elapsed.TotalMilliseconds:0} ms"
                : $"{(result.Error is { } e ? ModelErrorText.Korean(e) : "오류")}: {result.Message}";
            ConnectionTone = result.Reachable ? Tone.Good : Tone.Bad;
            CapabilityText = result.Reachable
                ? $"구조화 출력: {(result.StructuredFormats.Count > 0 ? string.Join(", ", result.StructuredFormats) : "서버가 알리지 않음(요청 시 확인·폴백)")} · 확인 시각 {result.CheckedAt.LocalDateTime:HH:mm:ss}"
                : string.Empty;
        }

        var (canSend, reason) = _context.Models.Vision(Selected);
        var visionAllowed = _context.Settings.Safety.AllowVision;
        VisionText = canSend && visionAllowed
            ? $"화면 인식(비전): 사용 가능 — {reason}"
            : $"화면 인식(비전): 꺼짐 — {(visionAllowed ? reason : "안전 설정에서 비전 사용이 꺼져 있습니다.")} 텍스트 전용 모델에는 UI 구조 정보만 보냅니다.";
        VisionTone = canSend && visionAllowed ? Tone.Good : Tone.Neutral;
    }

    // ---- model comparison ----

    [RelayCommand]
    private async Task RunComparisonAsync()
    {
        var profiles = CompareProfiles.Where(p => p.IsSelected).Select(p => p.Profile).ToList();
        var scenarios = Scenarios.Where(s => s.IsSelected).Select(s => s.Scenario).ToList();
        if (profiles.Count == 0 || scenarios.Count == 0)
        {
            _dialogs.Error("모델 비교", "비교할 연결과 시나리오를 하나 이상 고르세요.");
            return;
        }

        var clients = new List<(string, IModelClient)>();
        var problems = new List<string>();
        foreach (var profile in profiles)
        {
            var (client, problem) = _context.Models.CreateClient(profile);
            if (client is null)
            {
                problems.Add($"{profile.Name}: {problem}");
            }
            else
            {
                clients.Add((profile.Name, client));
            }
        }

        if (clients.Count == 0)
        {
            _dialogs.Error("모델 비교", string.Join("\n", problems));
            return;
        }

        var useReal = UseRealTestApp && CanUseRealTestApp;
        if (useReal && !_dialogs.Confirm("모델 비교", "실제 테스트 앱(OperatorTestTarget)을 시나리오마다 새로 열고 닫습니다. 승인이 필요한 단계는 자동으로 '거부'됩니다. 진행할까요?"))
        {
            return;
        }

        IsEvaluating = true;
        _evaluationCts = new CancellationTokenSource();
        var batch = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            var desktop = useReal ? _context.Desktop : MockDesktop.CreateDemo(openTestTarget: false);
            var evaluator = new ModelEvaluator(desktop);
            var progress = new Progress<string>(text => _ui.Post(() => EvaluationProgress = text));
            var results = await Task.Run(() => evaluator.RunAsync(clients, scenarios, _context.Settings.Safety, ct => PrepareTarget(desktop, useReal, ct), progress, _evaluationCts.Token)).ConfigureAwait(true);
            foreach (var result in results)
            {
                _context.Store.AddEvalRun(new EvalRunRecord(batch, DateTimeOffset.Now, result.ProfileName, result.ModelId, result.ScenarioName, result.IsMock, result.PlanValid,
                    result.GuiSuccess, result.Steps, result.DurationMs, result.ModelCalls, result.ModelErrors, result.WrongTargets, result.Interventions, result.Status, result.Note));
            }

            EvaluationProgress = $"완료: {results.Count}회 실행{(problems.Count > 0 ? " · 제외: " + string.Join("; ", problems) : string.Empty)}" + (useReal ? string.Empty : " (가상 테스트 앱 — 실제 GUI 결과 아님)");
            LoadEvaluations();
        }
        catch (OperationCanceledException)
        {
            EvaluationProgress = "모델 비교를 중단했습니다.";
        }
        finally
        {
            foreach (var (_, client) in clients)
            {
                (client as IDisposable)?.Dispose();
            }

            IsEvaluating = false;
            _evaluationCts.Dispose();
            _evaluationCts = null;
        }
    }

    [RelayCommand]
    private void StopComparison() => _evaluationCts?.Cancel();

    private static async Task<EvaluationTarget> PrepareTarget(Core.Automation.IDesktopAutomation desktop, bool real, CancellationToken cancellationToken)
    {
        if (desktop is MockDesktop mock)
        {
            var window = mock.AddWindow(MockApps.CreateTestTarget(mock));
            return new EvaluationTarget(window.Handle, () =>
            {
                mock.CloseWindow(window.Handle);
                return Task.CompletedTask;
            });
        }

        var launch = await desktop.LaunchAsync("OperatorTestTarget.exe", ["OperatorTestTarget"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (!launch.Success || launch.WindowHandle == 0)
        {
            throw new InvalidOperationException("테스트 앱을 열지 못했습니다: " + launch.Message);
        }

        return new EvaluationTarget(launch.WindowHandle, () =>
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(launch.ProcessId);
                if (string.Equals(process.ProcessName, "OperatorTestTarget", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return Task.CompletedTask;
        });
    }

    private void LoadEvaluations()
    {
        EvalRuns.Clear();
        EvalSummary.Clear();
        var runs = _context.Store.ListEvalRuns(500);
        foreach (var run in runs.Take(200))
        {
            EvalRuns.Add(run);
        }

        foreach (var group in runs.GroupBy(r => (r.ProfileName, r.ModelId, r.IsMock)))
        {
            var list = group.ToList();
            EvalSummary.Add(new EvalSummaryRow(group.Key.ProfileName, group.Key.ModelId, group.Key.IsMock, list.Count, list.Count(r => r.PlanValid), list.Count(r => r.GuiSuccess),
                list.Average(r => r.Steps), list.Average(r => r.DurationMs) / 1000.0, list.Sum(r => r.ModelCalls), list.Sum(r => r.ModelErrors), list.Sum(r => r.WrongTargets), list.Sum(r => r.Interventions)));
        }
    }
}
