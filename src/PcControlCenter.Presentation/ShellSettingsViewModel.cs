using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcControlCenter.Core;

namespace PcControlCenter.Presentation;

public sealed record DataFolderRow(string Module, string Origin, string Folder, string Database, string Size);

/// <summary>Common settings (module switches, shared sensors, scale) and the data folders. Module settings stay on module pages.</summary>
public sealed partial class ShellSettingsViewModel : ObservableObject
{
    private readonly ShellSettingsStore _store;
    private readonly ShellSettings _settings;
    private readonly ShellPaths _paths;
    private readonly IShellDialogs _dialogs;

    public ShellSettingsViewModel(ShellSettingsStore store, ShellSettings settings, ShellPaths paths, IShellDialogs dialogs)
    {
        _store = store;
        _settings = settings;
        _paths = paths;
        _dialogs = dialogs;
        Load();
    }

    [ObservableProperty] public partial bool LocalAiEnabled { get; set; }
    [ObservableProperty] public partial bool EconomicsEnabled { get; set; }
    [ObservableProperty] public partial bool DiagnosticsEnabled { get; set; }
    [ObservableProperty] public partial bool OperatorEnabled { get; set; }
    [ObservableProperty] public partial bool GpuEnabled { get; set; }
    [ObservableProperty] public partial bool SystemEnabled { get; set; }
    [ObservableProperty] public partial int MaxIntervalMs { get; set; }
    [ObservableProperty] public partial double FontScale { get; set; }
    [ObservableProperty] public partial int OperatorRestarts { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    public string DataRoot => _paths.Root;
    public string DataMode => _paths.IsPortable ? "포터블 (실행 파일 옆 data 폴더)" : "사용자 폴더";
    public string SettingsFile => _store.Path;
    public ObservableCollection<DataFolderRow> Folders { get; } = [];

    public string DataPolicy =>
        "각 모듈은 원래 프로그램과 같은 형식의 데이터베이스를 자기 폴더에 따로 둡니다 (lapc.db, gpc.db, wdl.db, wao.db). 데이터베이스를 하나로 합치지 않으며, "
        + "기존 프로그램(A·B·C·E)의 데이터 폴더는 읽기만 합니다. 보존 기간·백업·복원은 각 모듈의 설정 화면에서 그 모듈 데이터에만 적용됩니다.";

    /// <summary>Applied by the shell window (font scale) after a successful save.</summary>
    public event Action<ShellSettings>? Saved;

    public void Load()
    {
        LocalAiEnabled = _settings.Modules.LocalAi;
        EconomicsEnabled = _settings.Modules.GpuEconomics;
        DiagnosticsEnabled = _settings.Modules.Diagnostics;
        OperatorEnabled = _settings.Modules.Operator;
        GpuEnabled = _settings.Hardware.GpuEnabled;
        SystemEnabled = _settings.Hardware.SystemEnabled;
        MaxIntervalMs = _settings.Hardware.MaxIntervalMs;
        FontScale = _settings.Ui.FontScale;
        OperatorRestarts = _settings.Operator.MaxAutomaticRestarts;
        RefreshFolders();
    }

    public void RefreshFolders()
    {
        Folders.Clear();
        foreach (var id in ModuleIds.All)
        {
            var db = _paths.ModuleDatabasePath(id);
            Folders.Add(new DataFolderRow(ModuleIds.DisplayName(id), ModuleIds.Origin(id), _paths.ModuleDirectory(id), Path.GetFileName(db), SizeOf(db)));
        }
    }

    [RelayCommand]
    private void Save()
    {
        var restartNeeded = LocalAiEnabled != _settings.Modules.LocalAi || EconomicsEnabled != _settings.Modules.GpuEconomics
                            || DiagnosticsEnabled != _settings.Modules.Diagnostics || OperatorEnabled != _settings.Modules.Operator
                            || GpuEnabled != _settings.Hardware.GpuEnabled || SystemEnabled != _settings.Hardware.SystemEnabled
                            || MaxIntervalMs != _settings.Hardware.MaxIntervalMs;
        _settings.Modules.LocalAi = LocalAiEnabled;
        _settings.Modules.GpuEconomics = EconomicsEnabled;
        _settings.Modules.Diagnostics = DiagnosticsEnabled;
        _settings.Modules.Operator = OperatorEnabled;
        _settings.Hardware.GpuEnabled = GpuEnabled;
        _settings.Hardware.SystemEnabled = SystemEnabled;
        _settings.Hardware.MaxIntervalMs = MaxIntervalMs;
        _settings.Ui.FontScale = FontScale;
        _settings.Operator.MaxAutomaticRestarts = OperatorRestarts;
        var notes = _settings.Normalize();
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("설정 저장 실패", ex.Message);
            return;
        }

        Load();
        Saved?.Invoke(_settings);
        StatusText = string.Join(" ", notes.Append(restartNeeded ? "저장했습니다. 모듈·센서 변경은 프로그램을 다시 시작하면 적용됩니다." : "저장했습니다."));
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenModuleFolder(DataFolderRow? row)
    {
        if (row is not null)
        {
            _dialogs.OpenFolder(row.Folder);
        }
    }

    private static string SizeOf(string database)
    {
        long total = 0;
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
        {
            try
            {
                if (File.Exists(file))
                {
                    total += new FileInfo(file).Length;
                }
            }
            catch (IOException)
            {
            }
        }

        return total == 0 ? "없음" : total < 1024 * 1024 ? $"{total / 1024.0:0.0} KB" : $"{total / (1024.0 * 1024):0.0} MB";
    }
}
