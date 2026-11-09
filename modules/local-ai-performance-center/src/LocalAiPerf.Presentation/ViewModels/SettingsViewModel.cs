using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Export;
using LocalAiPerf.Presentation.Services;
using LocalAiPerf.Recording;
using LocalAiPerf.Storage;
using LocalAiPerf.Telemetry;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>Collection, retention and connection settings; data folder, backup/restore, integrity check and diagnostics.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly MonitoringService _service;
    private readonly SettingsStore _store;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _ui;
    private readonly MemoryLogSink? _logSink;

    public SettingsViewModel(MonitoringService service, SettingsStore store, IDialogService dialogs, IShellService shell, IUiDispatcher ui, MemoryLogSink? logSink)
    {
        _service = service;
        _store = store;
        _dialogs = dialogs;
        _shell = shell;
        _ui = ui;
        _logSink = logSink;
        LoadFrom(service.Settings);
    }

    public override string Title => "설정";
    public override string Glyph => "";

    public ObservableCollection<string> Messages { get; } = [];
    public ObservableCollection<ValueRow> DatabaseInfo { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];

    [ObservableProperty] public partial int IntervalMs { get; set; }
    [ObservableProperty] public partial bool GpuEnabled { get; set; }
    [ObservableProperty] public partial bool SystemEnabled { get; set; }
    [ObservableProperty] public partial int RetentionDays { get; set; }
    [ObservableProperty] public partial int MaxDatabaseSizeMb { get; set; }
    [ObservableProperty] public partial bool StrataEnabled { get; set; }
    [ObservableProperty] public partial string StrataUrl { get; set; } = "";
    [ObservableProperty] public partial string KeyVariable { get; set; } = "";
    [ObservableProperty] public partial bool StrataAutoFindPort { get; set; }
    [ObservableProperty] public partial int TimeoutMs { get; set; }
    [ObservableProperty] public partial int MetadataRefreshSeconds { get; set; }
    [ObservableProperty] public partial bool AutoRecord { get; set; }
    [ObservableProperty] public partial bool AutoSplit { get; set; }
    [ObservableProperty] public partial double DegradationThreshold { get; set; }
    [ObservableProperty] public partial int MinDegradationSeconds { get; set; }
    [ObservableProperty] public partial int BaselineWindowSeconds { get; set; }
    [ObservableProperty] public partial double HighGpuTemp { get; set; }
    [ObservableProperty] public partial int LiveChartMinutes { get; set; }
    [ObservableProperty] public partial int GpuIndex { get; set; }
    [ObservableProperty] public partial string DataDirectory { get; set; } = "";
    [ObservableProperty] public partial string KeyStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public override Task OnActivatedAsync()
    {
        LoadFrom(_service.Settings);
        Notices.Clear();
        foreach (var notice in _service.Notices)
        {
            Notices.Add(notice);
        }

        return RefreshDatabaseAsync();
    }

    private void LoadFrom(AppSettings s)
    {
        IntervalMs = s.Sampling.IntervalMs;
        GpuEnabled = s.Sampling.GpuEnabled;
        SystemEnabled = s.Sampling.SystemEnabled;
        RetentionDays = s.Retention.RetentionDays;
        MaxDatabaseSizeMb = s.Retention.MaxDatabaseSizeMb;
        StrataEnabled = s.Strata.Enabled;
        StrataUrl = s.Strata.BaseUrl;
        KeyVariable = s.Strata.ApiKeyEnvironmentVariable ?? "";
        StrataAutoFindPort = s.Strata.AutoFindPort;
        TimeoutMs = s.Strata.TimeoutMs;
        MetadataRefreshSeconds = s.Strata.MetadataRefreshSeconds;
        AutoRecord = s.Recording.AutoRecordOnStartup;
        AutoSplit = s.Recording.AutoSplitOnRuntimeChange;
        DegradationThreshold = s.Analysis.DegradationThresholdPercent;
        MinDegradationSeconds = s.Analysis.MinDegradationSeconds;
        BaselineWindowSeconds = s.Analysis.BaselineWindowSeconds;
        HighGpuTemp = s.Analysis.HighGpuTempC;
        LiveChartMinutes = s.Ui.LiveChartMinutes;
        GpuIndex = s.Ui.SelectedGpuIndex;
        DataDirectory = _service.Paths.DataDirectory + (_service.Paths.IsPortable ? "  (포터블 모드)" : "");
        UpdateKeyStatus();
    }

    partial void OnKeyVariableChanged(string value) => UpdateKeyStatus();

    private void UpdateKeyStatus()
    {
        var name = KeyVariable?.Trim();
        KeyStatus = string.IsNullOrEmpty(name)
            ? "키를 사용하지 않습니다 (로컬 기본 설정의 Strata는 키 없이 응답합니다)."
            : !AppSettings.IsValidEnvironmentVariableName(name)
                ? "환경 변수 이름 형식이 올바르지 않습니다."
                : string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))
                    ? $"{name}: 값 없음. 키가 필요한 Strata라면 이 이름의 사용자 환경 변수를 만들고 앱을 다시 시작하세요. 데스크톱 앱 엔진은 같은 값을 STRATA_TELEMETRY_KEY로 받아야 합니다(Strata 앱 재시작 필요). 키는 이 앱에 저장되지 않습니다."
                    : $"{name}: 값 있음 (내용은 표시하거나 저장하지 않음)";
    }

    public AppSettings BuildSettings()
    {
        var s = _service.Settings.Clone();
        s.Sampling.IntervalMs = IntervalMs;
        s.Sampling.GpuEnabled = GpuEnabled;
        s.Sampling.SystemEnabled = SystemEnabled;
        s.Retention.RetentionDays = RetentionDays;
        s.Retention.MaxDatabaseSizeMb = MaxDatabaseSizeMb;
        s.Strata.Enabled = StrataEnabled;
        s.Strata.BaseUrl = StrataUrl;
        s.Strata.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(KeyVariable) ? null : KeyVariable.Trim();
        s.Strata.AutoFindPort = StrataAutoFindPort;
        s.Strata.TimeoutMs = TimeoutMs;
        s.Strata.MetadataRefreshSeconds = MetadataRefreshSeconds;
        s.Recording.AutoRecordOnStartup = AutoRecord;
        s.Recording.AutoSplitOnRuntimeChange = AutoSplit;
        s.Analysis.DegradationThresholdPercent = DegradationThreshold;
        s.Analysis.MinDegradationSeconds = MinDegradationSeconds;
        s.Analysis.BaselineWindowSeconds = BaselineWindowSeconds;
        s.Analysis.HighGpuTempC = HighGpuTemp;
        s.Ui.LiveChartMinutes = LiveChartMinutes;
        s.Ui.SelectedGpuIndex = GpuIndex;
        return s;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Messages.Clear();
        if (!EndpointValidator.TryNormalize(StrataUrl, out _, out var urlError))
        {
            Messages.Add($"Strata 주소 오류: {urlError}");
            return;
        }

        var settings = BuildSettings();
        foreach (var note in settings.Normalize())
        {
            Messages.Add(note);
        }

        IsBusy = true;
        try
        {
            _store.Save(settings);
            await _service.ApplySettingsAsync(settings);
            LoadFrom(_service.Settings);
            Messages.Add("저장하고 적용했습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Messages.Add($"설정을 저장하지 못했습니다: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        LoadFrom(_service.Settings);
        Messages.Clear();
        Messages.Add("저장된 설정으로 되돌렸습니다.");
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        if (!_dialogs.Confirm("기본값으로", "화면의 설정 값을 기본값으로 바꿉니다. [저장]을 눌러야 적용됩니다. 계속할까요?"))
        {
            return;
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        LoadFrom(defaults);
        DataDirectory = _service.Paths.DataDirectory;
        Messages.Clear();
        Messages.Add("기본값을 불러왔습니다. 적용하려면 [저장]을 누르세요.");
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(_service.Paths.DataDirectory);

    [RelayCommand]
    private async Task RefreshDatabaseAsync()
    {
        DatabaseInfo.Clear();
        if (_service.Store is not { } store)
        {
            DatabaseInfo.Add(new ValueRow("상태", _service.StorageProblem ?? "데이터베이스를 사용하지 않습니다."));
            return;
        }

        try
        {
            var stats = await Task.Run(store.GetStats);
            DatabaseInfo.Add(new ValueRow("파일", stats.Path));
            DatabaseInfo.Add(new ValueRow("크기", $"{Fmt.Bytes(stats.FileBytes)} (사용 {Fmt.Bytes(stats.UsedBytes)}, WAL {Fmt.Bytes(stats.WalBytes)}, 빈 공간 {Fmt.Bytes(stats.FreeBytes)})"));
            DatabaseInfo.Add(new ValueRow("상한", $"{_service.Settings.Retention.MaxDatabaseSizeMb} MB, 보존 {_service.Settings.Retention.RetentionDays}일"));
            DatabaseInfo.Add(new ValueRow("세션", $"{stats.Sessions:N0}개"));
            DatabaseInfo.Add(new ValueRow("샘플", $"시스템 {stats.SystemSamples:N0} · GPU {stats.GpuSamples:N0} · 런타임 {stats.RuntimeSamples:N0} · 요청 {stats.Requests:N0} · 이벤트 {stats.Events:N0}"));
            DatabaseInfo.Add(new ValueRow("기간", $"{Fmt.DateTime(stats.Oldest)} ~ {Fmt.DateTime(stats.Newest)}"));
        }
        catch (Exception ex)
        {
            DatabaseInfo.Add(new ValueRow("오류", ex.Message));
        }
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var path = _dialogs.SaveFile("데이터베이스 백업", "SQLite 백업 (*.db)|*.db", $"lapc-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db", _service.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        await RunAsync("백업", async () =>
        {
            // The save dialog already asked before replacing an existing file.
            await _service.BackupAsync(path, overwrite: true);
            var validation = TelemetryStore.ValidateBackup(path);
            return $"백업 완료: {path}\n검증: {validation.Message}, 세션 {validation.Sessions}개";
        });
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var path = _dialogs.OpenFile("백업에서 복원", "SQLite 백업 (*.db)|*.db|모든 파일 (*.*)|*.*", _service.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        var validation = await Task.Run(() => TelemetryStore.ValidateBackup(path));
        if (!validation.IsValid)
        {
            _dialogs.Error("복원할 수 없음", $"이 파일은 복원할 수 있는 백업이 아닙니다: {validation.Message}");
            return;
        }

        if (!_dialogs.Confirm("백업에서 복원",
                $"백업: {path}\n세션 {validation.Sessions}개, 기간 {Fmt.DateTime(validation.Oldest)} ~ {Fmt.DateTime(validation.Newest)}\n\n" +
                "현재 데이터베이스를 이 백업으로 바꿉니다. 진행 중인 기록은 먼저 종료됩니다.\n" +
                "현재 데이터베이스는 삭제하지 않고 같은 폴더에 'lapc.before-restore-날짜.db'로 보관합니다.\n\n복원할까요?"))
        {
            return;
        }

        await RunAsync("복원", async () =>
        {
            var aside = await _service.RestoreAsync(path);
            return $"복원 완료.\n이전 데이터베이스 보관 위치: {aside ?? "(없었음)"}";
        });
        await RefreshDatabaseAsync();
    }

    [RelayCommand]
    private async Task IntegrityCheckAsync()
    {
        if (_service.Store is not { } store)
        {
            _dialogs.Error("무결성 검사", _service.StorageProblem ?? "데이터베이스가 열려 있지 않습니다.");
            return;
        }

        await RunAsync("무결성 검사", async () =>
        {
            var result = await Task.Run(store.IntegrityCheck);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? "무결성 검사 결과: 정상 (ok)"
                : $"무결성 검사에서 문제가 발견되었습니다:\n{result}\n\n백업을 만들고, 문제가 계속되면 앱을 다시 시작하세요 (손상된 파일은 자동으로 옆에 보관되고 새 DB가 만들어집니다).";
        });
    }

    [RelayCommand]
    private async Task ApplyRetentionAsync()
    {
        if (!_dialogs.Confirm("보존 정책 지금 적용",
                $"보존 기간 {_service.Settings.Retention.RetentionDays}일이 지난 데이터와, 상한 {_service.Settings.Retention.MaxDatabaseSizeMb} MB를 넘는 가장 오래된 세션을 삭제합니다. 계속할까요?"))
        {
            return;
        }

        await RunAsync("보존 정책", async () =>
        {
            var result = await _service.ApplyRetentionNowAsync();
            return result is null
                ? "데이터베이스가 열려 있지 않습니다."
                : $"세션 {result.SessionsDeleted}개, 행 {result.RowsDeleted:N0}개 삭제. 사용량 {Fmt.Bytes(result.BytesBefore)} → {Fmt.Bytes(result.BytesAfter)}" +
                  (result.Notes.Count > 0 ? "\n" + string.Join("\n", result.Notes) : "");
        });
        await RefreshDatabaseAsync();
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = _dialogs.SaveFile("진단 정보 내보내기", "ZIP 파일 (*.zip)|*.zip", $"lapc-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip", _service.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        await RunAsync("진단 정보", async () =>
        {
            DatabaseStats? stats = null;
            if (_service.Store is { } store)
            {
                stats = await Task.Run(store.GetStats);
            }

            var capabilities = _service.Hardware?.Capabilities ?? HardwareMonitor.NotCollected;
            DiagnosticsBundle.Create(path, _service.Settings, _service.Paths, _service.HardwareInfo, capabilities, _service.LatestRuntime, stats,
                _service.Notices, _logSink?.Snapshot() ?? []);
            return $"진단 정보를 저장했습니다: {path}\n(API 키 값, 프롬프트, 응답은 포함되지 않습니다.)";
        });
    }

    private async Task RunAsync(string title, Func<Task<string>> action)
    {
        IsBusy = true;
        try
        {
            var message = await action();
            _ui.Post(() => _dialogs.Info(title, message));
        }
        catch (Exception ex)
        {
            _ui.Post(() => _dialogs.Error(title, $"{title} 실패: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
