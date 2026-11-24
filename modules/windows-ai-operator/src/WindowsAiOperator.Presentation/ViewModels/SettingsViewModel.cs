using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation.ViewModels;

/// <summary>General settings, data folder, screenshots (off by default), retention, backup, restore and integrity check.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly IDialogService _dialogs;
    private readonly IAppHost _host;
    private readonly Func<bool> _isTaskRunning;

    public SettingsViewModel(OperatorContext context, IDialogService dialogs, IAppHost host, Func<bool> isTaskRunning)
        : base("설정", "", "settings")
    {
        _context = context;
        _dialogs = dialogs;
        _host = host;
        _isTaskRunning = isTaskRunning;
        LogLevels = [new("자세히(Debug)", LogLevel.Debug), new("보통(Info)", LogLevel.Info), new("경고 이상", LogLevel.Warning), new("오류만", LogLevel.Error)];
        Load();
    }

    public IReadOnlyList<Option<LogLevel>> LogLevels { get; }
    public ObservableCollection<BackupInfo> Backups { get; } = [];
    public string DataFolder => _context.Paths.DataDirectory;
    public string DataMode => _context.Paths.IsPortable ? "포터블 모드(실행 파일 옆 data 폴더)" : "사용자 폴더";
    public string Version => $"{AppInfo.ProductName} {AppInfo.Version}";
    public string Components => "FlaUI 5.0.0 (MIT) · CommunityToolkit.Mvvm 8.4.2 (MIT) · Microsoft.Data.Sqlite (MIT) · .NET 10 / WPF (MIT). 자세한 내용은 THIRD-PARTY-NOTICES.md";

    [ObservableProperty]
    public partial bool SaveScreenshots { get; set; }

    [ObservableProperty]
    public partial int ScreenshotRetentionDays { get; set; }

    [ObservableProperty]
    public partial int HistoryRetentionDays { get; set; }

    [ObservableProperty]
    public partial int AutoBackupDays { get; set; }

    [ObservableProperty]
    public partial Option<LogLevel>? SelectedLogLevel { get; set; }

    [ObservableProperty]
    public partial double FontScale { get; set; }

    [ObservableProperty]
    public partial BackupInfo? SelectedBackup { get; set; }

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    [ObservableProperty]
    public partial string StoreSummary { get; set; } = string.Empty;

    public override void OnNavigatedTo() => Load();

    [RelayCommand]
    private void Load()
    {
        var g = _context.Settings.General;
        SaveScreenshots = g.SaveScreenshots;
        ScreenshotRetentionDays = g.ScreenshotRetentionDays;
        HistoryRetentionDays = g.HistoryRetentionDays;
        AutoBackupDays = g.AutoBackupDays;
        SelectedLogLevel = LogLevels.FirstOrDefault(l => l.Value == g.LogLevel) ?? LogLevels[1];
        FontScale = g.FontScale;
        RefreshBackups();
        RefreshStatus();
    }

    [RelayCommand]
    private void Save()
    {
        if (SaveScreenshots && !_context.Settings.General.SaveScreenshots
            && !_dialogs.Confirm("스크린샷 저장", "화면 인식에 쓴 다른 프로그램의 스크린샷을 data\\screenshots에 저장합니다. 화면에 민감한 정보가 있으면 함께 저장됩니다. 켤까요?"))
        {
            SaveScreenshots = false;
        }

        var g = _context.Settings.General;
        g.SaveScreenshots = SaveScreenshots;
        g.ScreenshotRetentionDays = ScreenshotRetentionDays;
        g.HistoryRetentionDays = HistoryRetentionDays;
        g.AutoBackupDays = AutoBackupDays;
        g.LogLevel = SelectedLogLevel?.Value ?? LogLevel.Info;
        g.Theme = "Dark";
        g.FontScale = FontScale;
        var notes = _context.SaveSettings();
        _host.ApplyFontScale(g.FontScale);
        _host.ApplyTheme(g.Theme);
        ResultText = notes.Count == 0 ? "저장했습니다." : "저장했습니다: " + string.Join(" ", notes);
    }

    [RelayCommand]
    private void OpenDataFolder() => _host.OpenFolder(DataFolder);

    [RelayCommand]
    private void BackupNow()
    {
        try
        {
            var backup = _context.Store.Backup(_context.Paths.BackupDirectory);
            ResultText = $"백업했습니다: {Path.GetFileName(backup.Path)} ({backup.SizeBytes / 1024.0:0.0} KB, 검사 통과)";
            RefreshBackups();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ResultText = "백업 실패: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Restore()
    {
        if (SelectedBackup is null)
        {
            ResultText = "복원할 백업을 고르세요.";
            return;
        }

        if (_isTaskRunning())
        {
            ResultText = "작업이 실행 중이라 복원하지 않았습니다. 작업을 끝내거나 중단한 뒤 다시 시도하세요.";
            return;
        }

        if (!_dialogs.Confirm("백업에서 복원",
                $"현재 기록 데이터베이스를 '{Path.GetFileName(SelectedBackup.Path)}'({SelectedBackup.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}) 내용으로 바꿉니다.\n" +
                "바꾸기 전에 현재 데이터베이스를 자동으로 백업하므로, 잘못 복원했으면 그 백업에서 다시 복원할 수 있습니다.\n실행 중인 작업이 있으면 먼저 중단하세요. 계속할까요?"))
        {
            return;
        }

        try
        {
            var safety = _context.Store.RestoreFrom(SelectedBackup.Path, _context.Paths.BackupDirectory);
            ResultText = $"복원했습니다. 복원 전 데이터는 {Path.GetFileName(safety)}에 있습니다.";
            RefreshBackups();
            RefreshStatus();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ResultText = "복원하지 않았습니다: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CheckIntegrity()
    {
        var result = _context.Store.IntegrityCheck();
        ResultText = result == "ok" ? "무결성 검사 통과(ok)" : "무결성 문제: " + result;
    }

    [RelayCommand]
    private void ApplyRetention()
    {
        if (_dialogs.Confirm("오래된 기록 정리", $"{HistoryRetentionDays}일보다 오래된 완료 기록을 삭제합니다(실행 중이던 기록은 남김). 계속할까요?"))
        {
            var removed = _context.Store.ApplyRetention(HistoryRetentionDays);
            ResultText = $"기록 {removed}개를 정리했습니다.";
            RefreshStatus();
        }
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var backup in OperatorStore.ListBackups(_context.Paths.BackupDirectory))
        {
            Backups.Add(backup);
        }
    }

    private void RefreshStatus()
    {
        var status = _context.Store.GetStatus();
        StoreSummary = $"작업 {status.Tasks}개 · 단계 {status.Steps}개 · 이벤트 {status.Events}개 · 워크플로 {status.Workflows}개 · 모델 비교 {status.EvalRuns}회 · " +
                       $"{status.SizeBytes / 1024.0:0} KB · 스키마 v{status.SchemaVersion} · 검사 {status.Integrity}";
    }
}
