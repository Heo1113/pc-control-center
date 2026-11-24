using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Diagnostics;
using WinDiag.Collection;
using WinDiag.Core.Events;
using WinDiag.Core.Settings;
using WinDiag.Core.Time;
using WinDiag.Storage;

namespace WinDiag.Presentation.ViewModels;

public sealed partial class ChannelSettingRow(string name, bool enabled, string status, string detail, string oldest) : ObservableObject
{
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Detail { get; } = detail;
    public string Oldest { get; } = oldest;

    [ObservableProperty] public partial bool Enabled { get; set; } = enabled;
}

/// <summary>Collection targets and intervals, retention, notifications, privacy, backup and restore.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(ViewContext context) : base(context)
    {
        SeverityOptions =
        [
            new("경고 이상", DiagSeverity.Warning), new("오류 이상", DiagSeverity.Error), new("심각만", DiagSeverity.Critical),
        ];
        Load(context.Settings);
        ImportFrom = DateTime.Today.AddDays(-90);
        ImportTo = DateTime.Today;
    }

    public override string Title => "설정";
    public override string Glyph => "";

    public IReadOnlyList<Option<DiagSeverity>> SeverityOptions { get; }
    public ObservableCollection<ChannelSettingRow> Channels { get; } = [];
    public ObservableCollection<NameValueRow> DatabaseInfo { get; } = [];

    [ObservableProperty] public partial string NewChannel { get; set; } = "";
    [ObservableProperty] public partial string HistoryDays { get; set; } = "";
    [ObservableProperty] public partial string PollSeconds { get; set; } = "";
    [ObservableProperty] public partial bool CollectAllWarnings { get; set; }
    [ObservableProperty] public partial string ExcludedProviders { get; set; } = "";
    [ObservableProperty] public partial bool SensorsEnabled { get; set; }
    [ObservableProperty] public partial string SensorInterval { get; set; } = "";
    [ObservableProperty] public partial bool GpuEnabled { get; set; }
    [ObservableProperty] public partial bool SystemEnabled { get; set; }
    [ObservableProperty] public partial string EventDays { get; set; } = "";
    [ObservableProperty] public partial string SensorDays { get; set; } = "";
    [ObservableProperty] public partial string WarnDatabaseMb { get; set; } = "";
    [ObservableProperty] public partial bool NotificationsEnabled { get; set; }
    [ObservableProperty] public partial bool TrayBalloon { get; set; }
    [ObservableProperty] public partial Option<DiagSeverity>? MinimumSeverity { get; set; }
    [ObservableProperty] public partial string QuietMinutes { get; set; } = "";
    [ObservableProperty] public partial bool MaskAtCollection { get; set; }
    [ObservableProperty] public partial bool UseUtc { get; set; }
    [ObservableProperty] public partial string SnapshotHours { get; set; } = "";
    [ObservableProperty] public partial bool IncludeDrivers { get; set; }
    [ObservableProperty] public partial string CorrelationSeconds { get; set; } = "";
    [ObservableProperty] public partial string SensorWindowMinutes { get; set; } = "";
    [ObservableProperty] public partial string RepeatThreshold { get; set; } = "";
    [ObservableProperty] public partial string HighGpuTemp { get; set; } = "";
    [ObservableProperty] public partial DateTime? ImportFrom { get; set; }
    [ObservableProperty] public partial DateTime? ImportTo { get; set; }
    [ObservableProperty] public partial string ImportProgress { get; set; } = "";
    [ObservableProperty] public partial string DataFolder { get; set; } = "";
    [ObservableProperty] public partial string DumpStatus { get; set; } = "";

    public override Task OnActivatedAsync() => RefreshInfoAsync();

    partial void OnMaskAtCollectionChanged(bool value)
    {
        if (!value && Context.Settings.Privacy.MaskAtCollection)
        {
            StatusText = "주의: 마스킹을 끄면 이후 수집하는 이벤트의 사용자 이름·경로·SID 등이 원문 그대로 저장됩니다. 내보내기에는 항상 다시 가려집니다.";
        }
    }

    [RelayCommand]
    private void AddChannel()
    {
        var name = NewChannel.Trim();
        if (name.Length == 0 || Channels.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (string.Equals(name, CollectionSettings.SecurityChannel, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "보안(Security) 로그는 관리자 권한이 필요하고 민감한 정보가 많아 이 버전에서는 수집하지 않습니다.";
            return;
        }

        Channels.Add(new ChannelSettingRow(name, true, "저장 후 확인", "", Fmt.NA));
        NewChannel = "";
    }

    [RelayCommand]
    private async Task Save()
    {
        var settings = Context.Settings.Clone();
        var errors = new List<string>();
        int Parse(string text, string name, int fallback)
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            errors.Add(name);
            return fallback;
        }

        settings.Collection.Channels = Channels.Select(c => new ChannelSetting { Name = c.Name, Enabled = c.Enabled }).ToList();
        settings.Collection.HistoryDays = Parse(HistoryDays, "과거 로그 조회 기간", settings.Collection.HistoryDays);
        settings.Collection.PollSeconds = Parse(PollSeconds, "새 이벤트 확인 간격", settings.Collection.PollSeconds);
        settings.Collection.CollectAllWarnings = CollectAllWarnings;
        settings.Collection.ExcludedProviders = ExcludedProviders.Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        settings.Sensors.Enabled = SensorsEnabled;
        settings.Sensors.IntervalSeconds = Parse(SensorInterval, "센서 기록 간격", settings.Sensors.IntervalSeconds);
        settings.Sensors.GpuEnabled = GpuEnabled;
        settings.Sensors.SystemEnabled = SystemEnabled;
        settings.Retention.EventDays = Parse(EventDays, "이벤트 보존 기간", settings.Retention.EventDays);
        settings.Retention.SensorDays = Parse(SensorDays, "센서 보존 기간", settings.Retention.SensorDays);
        settings.Retention.WarnDatabaseMb = Parse(WarnDatabaseMb, "DB 크기 경고 기준", settings.Retention.WarnDatabaseMb);
        settings.Notifications.Enabled = NotificationsEnabled;
        settings.Notifications.TrayBalloon = TrayBalloon;
        settings.Notifications.MinimumSeverity = MinimumSeverity?.Value ?? DiagSeverity.Error;
        settings.Notifications.RepeatQuietMinutes = Parse(QuietMinutes, "반복 알림 묶음 시간", settings.Notifications.RepeatQuietMinutes);
        settings.Privacy.MaskAtCollection = MaskAtCollection;
        settings.Ui.UseUtc = UseUtc;
        settings.Snapshots.IntervalHours = Parse(SnapshotHours, "스냅샷 간격", settings.Snapshots.IntervalHours);
        settings.Snapshots.IncludeDrivers = IncludeDrivers;
        settings.Analysis.CorrelationWindowSeconds = Parse(CorrelationSeconds, "동시 발생 판정 구간", settings.Analysis.CorrelationWindowSeconds);
        settings.Analysis.SensorWindowMinutes = Parse(SensorWindowMinutes, "이벤트 전후 센서 구간", settings.Analysis.SensorWindowMinutes);
        settings.Analysis.RepeatThreshold = Parse(RepeatThreshold, "반복 판정 기준", settings.Analysis.RepeatThreshold);
        settings.Analysis.HighGpuTempC = Parse(HighGpuTemp, "GPU 고온 기준", settings.Analysis.HighGpuTempC);
        if (errors.Count > 0)
        {
            StatusText = "숫자가 아닌 값이 있어 저장하지 않았습니다: " + string.Join(", ", errors);
            return;
        }

        var notes = settings.Normalize();
        try
        {
            await Task.Run(() => Context.SettingsStore.Save(settings));
            await Context.Engine.ApplySettingsAsync(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "설정 파일을 저장하지 못했습니다: " + ex.Message;
            return;
        }

        Fmt.Display = settings.Ui.UseUtc ? TimeDisplay.Utc : TimeDisplay.Local;
        Load(settings);
        StatusText = notes.Count > 0 ? "저장했습니다. 조정된 값: " + string.Join(" ", notes) : "저장했습니다. 수집 설정은 다음 확인 주기부터 적용됩니다.";
    }

    [RelayCommand]
    private void Revert()
    {
        Load(Context.Settings);
        StatusText = "저장된 설정으로 되돌렸습니다.";
    }

    [RelayCommand]
    private async Task ImportHistory()
    {
        if (ImportFrom is not { } from || ImportTo is not { } to || to < from)
        {
            StatusText = "가져올 기간을 확인하세요.";
            return;
        }

        await Busy(async () =>
        {
            var progress = new Progress<CollectionProgress>(p => ImportProgress = $"{p.Channel}: {p.Read:N0}건 읽음, {p.Inserted:N0}건 새로 저장");
            var report = await Context.Engine.ImportHistoryAsync(new DateTimeOffset(from.Date), new DateTimeOffset(to.Date.AddDays(1)), progress);
            if (report is null)
            {
                ImportProgress = Context.Engine.EventCollectionUnavailableReason ?? "가져오지 못했습니다.";
                return;
            }

            var retention = report.Channels.SelectMany(c => c.Gaps).Where(g => g.Kind == GapKinds.LogRetention).ToList();
            ImportProgress = $"완료: {report.Read:N0}건 읽음, 새로 저장 {report.Inserted:N0}건, 이미 있던 이벤트 {report.Duplicates:N0}건."
                             + string.Join("", report.Channels.Where(c => c.Status != ChannelStatus.Ok).Select(c => $" {c.Channel}: {ChannelStatusText.Name(c.Status)}."))
                             + (retention.Count > 0 ? $" 일부 로그는 요청한 기간 전체가 Windows에 남아 있지 않습니다({string.Join(", ", retention.Select(g => g.Scope))})." : "");
            await RefreshInfoAsync();
        });
    }

    [RelayCommand]
    private async Task TakeSnapshot()
    {
        await Busy(async () =>
        {
            var result = await Context.Engine.TakeSnapshotAsync("manual");
            StatusText = result.IsNew ? "구성 스냅샷을 저장했습니다 (이전과 다름 또는 첫 스냅샷)." : "구성이 이전 스냅샷과 같습니다 (확인 시각만 갱신).";
        });
    }

    [RelayCommand]
    private async Task ScanDumps()
    {
        await Busy(async () =>
        {
            var result = await Context.Engine.ScanDumpsAsync();
            DumpStatus = $"덤프 파일 {result.Files.Count}개 확인. " + string.Join(" ", result.Notes);
        });
    }

    [RelayCommand]
    private async Task ApplyRetention()
    {
        var settings = Context.Settings;
        var text = settings.Retention.EventDays > 0
            ? $"이벤트는 {settings.Retention.EventDays}일, 센서 기록은 {settings.Retention.SensorDays}일보다 오래된 행을 정리합니다."
            : $"이벤트는 보존 기간 제한이 없어 유지하고, 센서 기록만 {settings.Retention.SensorDays}일보다 오래된 행을 정리합니다.";
        if (!Context.Dialogs.Confirm("보존 정책 적용", text + "\n사용자 기록·기준선·메모·스냅샷과 데이터베이스 파일 자체는 삭제하지 않습니다. 진행할까요?"))
        {
            return;
        }

        await Busy(async () =>
        {
            var result = await Task.Run(() => Context.Store.ApplyRetention(settings.Retention.EventDays, settings.Retention.SensorDays));
            StatusText = result.Notes.Count == 0 ? "정리할 오래된 기록이 없습니다." : string.Join(" ", result.Notes);
            await RefreshInfoAsync();
        });
    }

    [RelayCommand]
    private async Task Backup()
    {
        var path = Context.Dialogs.SaveFile("데이터베이스 백업", "SQLite 데이터베이스 (*.db)|*.db", $"wdl-backup-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.db",
            Context.Engine.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        await Busy(async () =>
        {
            await Task.Run(() => Context.Store.BackupTo(path, overwrite: File.Exists(path)));
            var validation = DiagnosticsStore.ValidateBackup(path);
            StatusText = validation.IsValid ? $"백업했고 검증했습니다: {Path.GetFileName(path)} (이벤트 {validation.Events:N0}건)" : "백업 파일 검증 실패: " + validation.Message;
        });
    }

    [RelayCommand]
    private void ValidateBackup()
    {
        var path = Context.Dialogs.OpenFile("백업 파일 검증", "SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*", Context.Engine.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        var v = DiagnosticsStore.ValidateBackup(path);
        Context.Dialogs.Info("백업 검증", v.IsValid
            ? $"정상입니다.\n스키마 {v.SchemaVersion}, 이벤트 {v.Events:N0}건, 기간 {Fmt.Time(v.Oldest)} ~ {Fmt.Time(v.Newest)}"
            : "사용할 수 없는 백업입니다: " + v.Message);
    }

    [RelayCommand]
    private async Task Restore()
    {
        var path = Context.Dialogs.OpenFile("백업에서 복원", "SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*", Context.Engine.Paths.BackupDirectory);
        if (path is null)
        {
            return;
        }

        var v = DiagnosticsStore.ValidateBackup(path);
        if (!v.IsValid)
        {
            Context.Dialogs.Error("복원할 수 없음", "백업 파일이 올바르지 않습니다: " + v.Message);
            return;
        }

        if (!Context.Dialogs.Confirm("백업에서 복원", $"이 백업(이벤트 {v.Events:N0}건, {Fmt.Time(v.Oldest)} ~ {Fmt.Time(v.Newest)})으로 데이터베이스를 바꿀까요?\n\n" +
                "현재 데이터베이스는 삭제하지 않고 'before-restore' 이름으로 옆에 보관합니다. 복원 후 프로그램이 다시 시작됩니다."))
        {
            return;
        }

        if (Context.Host is null)
        {
            StatusText = "이 환경에서는 복원할 수 없습니다.";
            return;
        }

        await Context.Host.RestoreAndRestartAsync(path);
    }

    [RelayCommand]
    private async Task IntegrityCheck()
    {
        await Busy(async () =>
        {
            var result = await Task.Run(() => Context.Store.IntegrityCheck());
            StatusText = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? "무결성 검사: 정상" : "무결성 검사 결과: " + result;
        });
    }

    [RelayCommand]
    private void OpenDataFolder() => Context.Shell.OpenFolder(Context.Engine.Paths.DataDirectory);

    [RelayCommand]
    private void OpenLogFolder() => Context.Shell.OpenFolder(Context.Engine.Paths.LogDirectory);

    [RelayCommand]
    private async Task LoadDemoData()
    {
        if (!Context.Dialogs.Confirm("가상 테스트 데이터 불러오기",
                "30일 분량의 가상 시나리오(이벤트, MOCK 센서, 스냅샷, 사용자 기록, 기준선)를 이 데이터베이스에 추가합니다.\n" +
                "모든 가상 이벤트는 '가상'으로 표시되고 메시지가 [가상]으로 시작하지만, 실제 기록과 같은 데이터베이스에 섞입니다. " +
                "화면 확인용으로는 별도 데이터 폴더(--data-dir)에서 실행하는 것을 권장합니다. 계속할까요?"))
        {
            return;
        }

        await Busy(async () =>
        {
            var report = await Context.Engine.SeedVirtualScenarioAsync();
            StatusText = $"가상 이벤트 {report.Inserted}건을 추가했습니다.";
            await RefreshInfoAsync();
        });
    }

    private void Load(AppSettings s)
    {
        var states = SafeStates();
        Replace(Channels, s.Collection.Channels.Select(c =>
        {
            var state = states.FirstOrDefault(x => string.Equals(x.Channel, c.Name, StringComparison.OrdinalIgnoreCase));
            return new ChannelSettingRow(c.Name, c.Enabled, state is null ? "아직 확인 안 함" : ChannelStatusText.Name(state.Status),
                state is null ? "" : ChannelStatusText.Explain(state.Status, c.Name), Fmt.Time(state?.OldestEventTime));
        }));
        HistoryDays = s.Collection.HistoryDays.ToString(CultureInfo.InvariantCulture);
        PollSeconds = s.Collection.PollSeconds.ToString(CultureInfo.InvariantCulture);
        CollectAllWarnings = s.Collection.CollectAllWarnings;
        ExcludedProviders = string.Join(", ", s.Collection.ExcludedProviders);
        SensorsEnabled = s.Sensors.Enabled;
        SensorInterval = s.Sensors.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        GpuEnabled = s.Sensors.GpuEnabled;
        SystemEnabled = s.Sensors.SystemEnabled;
        EventDays = s.Retention.EventDays.ToString(CultureInfo.InvariantCulture);
        SensorDays = s.Retention.SensorDays.ToString(CultureInfo.InvariantCulture);
        WarnDatabaseMb = s.Retention.WarnDatabaseMb.ToString(CultureInfo.InvariantCulture);
        NotificationsEnabled = s.Notifications.Enabled;
        TrayBalloon = s.Notifications.TrayBalloon;
        MinimumSeverity = SeverityOptions.FirstOrDefault(o => o.Value == s.Notifications.MinimumSeverity) ?? SeverityOptions[1];
        QuietMinutes = s.Notifications.RepeatQuietMinutes.ToString(CultureInfo.InvariantCulture);
        MaskAtCollection = s.Privacy.MaskAtCollection;
        UseUtc = s.Ui.UseUtc;
        SnapshotHours = s.Snapshots.IntervalHours.ToString(CultureInfo.InvariantCulture);
        IncludeDrivers = s.Snapshots.IncludeDrivers;
        CorrelationSeconds = s.Analysis.CorrelationWindowSeconds.ToString(CultureInfo.InvariantCulture);
        SensorWindowMinutes = s.Analysis.SensorWindowMinutes.ToString(CultureInfo.InvariantCulture);
        RepeatThreshold = s.Analysis.RepeatThreshold.ToString(CultureInfo.InvariantCulture);
        HighGpuTemp = s.Analysis.HighGpuTempC.ToString(CultureInfo.InvariantCulture);
        DataFolder = Context.Engine.Paths.DataDirectory + (Context.Engine.Paths.IsPortable ? " (포터블)" : "");
    }

    private IReadOnlyList<ChannelState> SafeStates()
    {
        try
        {
            return Context.Store.GetChannelStates();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Context.Log.Warn("ui", "채널 상태 읽기 실패", ex);
            return [];
        }
    }

    private Task RefreshInfoAsync() => Busy(async () =>
    {
        var stats = await Task.Run(() => Context.Store.GetStats());
        var engine = Context.Engine;
        Replace(DatabaseInfo,
        [
            new("데이터베이스", Fmt.Mb(stats.FileBytes) + (stats.WalBytes > 0 ? $" (+ WAL {Fmt.Mb(stats.WalBytes)})" : ""), engine.Paths.DatabasePath),
            new("이벤트", $"{stats.Events:N0}건" + (stats.VirtualEvents > 0 ? $" (가상 {stats.VirtualEvents:N0}건)" : ""), $"{Fmt.Time(stats.OldestEvent)} ~ {Fmt.Time(stats.NewestEvent)}"),
            new("센서 샘플", $"{stats.SensorSamples:N0}개", $"{Fmt.Time(stats.OldestSensor)} ~ {Fmt.Time(stats.NewestSensor)}"),
            new("구성 스냅샷", $"{stats.Snapshots:N0}개"),
            new("앱 실행 기록", $"{stats.Runs:N0}회"),
            new("수집 누락·제한 기록", $"{stats.Gaps:N0}건"),
            new("관리자 권한", Environment.IsPrivilegedProcess ? "예" : "아니요 (일반 사용자 권한)"),
        ]);
        Load(Context.Settings);
        if (engine.LastDumpScan is { } dumps)
        {
            DumpStatus = $"덤프 파일 {dumps.Files.Count}개 확인. " + string.Join(" ", dumps.Notes);
        }
    });
}
