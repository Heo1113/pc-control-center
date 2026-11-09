using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcControlCenter.Core;
using PcControlCenter.Migration;

namespace PcControlCenter.Presentation;

/// <summary>One original program's data folder and what happened with it.</summary>
public sealed partial class LegacySourceRow(LegacyLocation location) : ObservableObject
{
    public LegacyLocation Location { get; } = location;
    public string Program => ModuleIds.Origin(Location.ModuleId);
    public string Folder => Location.Directory;

    [ObservableProperty] public partial string State { get; set; } = "";
    [ObservableProperty] public partial Level Level { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial MigrationRehearsal? Rehearsal { get; set; }

    [ObservableProperty] public partial bool Exists { get; set; }

    public bool CanApply => Rehearsal is { Passed: true };
}

/// <summary>
/// 설정 › 데이터·이전: finds the original programs' data, rehearses a verified copy (nothing is changed), and applies it to
/// one module only after an explicit confirmation that names what is replaced and where the previous data is kept.
/// </summary>
public sealed partial class DataMigrationViewModel : ObservableObject
{
    private readonly LegacyMigrator _migrator;
    private readonly IModuleRestarter _modules;
    private readonly IShellDialogs _dialogs;
    private readonly IShellDispatcher _ui;
    private readonly ShellPaths _paths;

    public DataMigrationViewModel(LegacyMigrator migrator, IModuleRestarter modules, ShellPaths paths, IReadOnlyList<LegacyLocation> defaults, IShellDialogs dialogs,
        IShellDispatcher ui)
    {
        _migrator = migrator;
        _modules = modules;
        _paths = paths;
        _dialogs = dialogs;
        _ui = ui;
        foreach (var location in defaults)
        {
            Sources.Add(new LegacySourceRow(location));
        }

        Refresh();
    }

    public ObservableCollection<LegacySourceRow> Sources { get; } = [];
    public ObservableCollection<MigrationCheck> Checks { get; } = [];

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IncludeSettings { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RehearseCommand), nameof(ApplyCommand))]
    public partial bool IsBusy { get; set; }

    public string Policy =>
        "기존 프로그램(A·B·C·E)의 데이터 폴더는 읽기만 합니다. [검사·리허설]은 원본 파일을 바이트 단위로 복사해 SHA-256으로 같은지 확인한 뒤, 복사본으로만 무결성·스키마 버전·"
        + "모듈 자체 검증·모든 테이블의 행 수·주요 집계를 비교합니다. 원래 프로그램이 실행 중이면 진행하지 않습니다. [적용]을 눌러 확인해야만 통합 앱의 해당 모듈 데이터가 바뀌며, "
        + "그 전에 있던 통합 앱의 데이터는 같은 폴더에 보관(삭제하지 않음)되고 실패하면 자동으로 되돌립니다.";

    public string MigrationFolder => _paths.MigrationDirectory;

    [RelayCommand]
    private void Refresh()
    {
        foreach (var row in Sources)
        {
            var source = _migrator.Inspect(row.Location);
            row.Exists = source.Exists;
            if (!source.Exists)
            {
                row.State = "데이터 없음";
                row.Level = Level.Muted;
            }
            else if (source.OriginalProgramRunning)
            {
                row.State = $"원래 프로그램 실행 중 ({string.Join(", ", source.RunningProcesses)}) — 종료 후 가져올 수 있음";
                row.Level = Level.Warning;
            }
            else if (row.Rehearsal is null)
            {
                row.State = $"{source.DatabaseBytes / (1024.0 * 1024):0.0} MB{(source.HasWal ? " + WAL" : "")} · 리허설 전";
                row.Level = Level.Muted;
            }
        }
    }

    [RelayCommand]
    private void AddFolder()
    {
        var folder = _dialogs.PickFolder("기존 프로그램의 데이터 폴더 선택 (포터블 설치는 실행 파일 옆 data 폴더)");
        if (folder is null)
        {
            return;
        }

        if (LegacyMigrator.FromFolder(folder) is not { } location)
        {
            _dialogs.Error("데이터 없음", "선택한 폴더에서 lapc.db, gpc.db, wdl.db, wao.db를 찾지 못했습니다.");
            return;
        }

        if (Sources.All(s => !string.Equals(s.Location.DatabasePath, location.DatabasePath, StringComparison.OrdinalIgnoreCase)))
        {
            Sources.Add(new LegacySourceRow(location));
        }

        Refresh();
    }

    private bool CanRun(LegacySourceRow? row) => row is { Exists: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RehearseAsync(LegacySourceRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = $"{row.Program}: 검사·리허설 중…";
        try
        {
            var rehearsal = await Task.Run(() => _migrator.Rehearse(row.Location));
            _ui.Post(() =>
            {
                row.Rehearsal = rehearsal;
                row.State = rehearsal.Passed ? $"리허설 통과 · {rehearsal.TableCounts.Count}개 테이블 {rehearsal.TableCounts.Values.Sum():N0}행" : "리허설 실패 (원본·통합 앱 모두 변경 없음)";
                row.Level = rehearsal.Passed ? Level.Good : Level.Error;
                ShowChecks(rehearsal.Checks);
                StatusText = rehearsal.Passed
                    ? $"{row.Program}: 검증된 복사본을 만들었습니다 ({rehearsal.StagingDirectory}). 아직 아무것도 바꾸지 않았습니다."
                    : $"{row.Program}: 리허설에서 문제가 있어 진행하지 않았습니다.";
            });
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyAsync(LegacySourceRow? row)
    {
        if (row?.Rehearsal is not { Passed: true } rehearsal)
        {
            _dialogs.Info("적용할 수 없음", "먼저 [검사·리허설]을 통과해야 합니다.");
            return;
        }

        var module = ModuleIds.DisplayName(rehearsal.ModuleId);
        var target = _paths.ModuleDatabasePath(rehearsal.ModuleId);
        if (!_dialogs.Confirm("기존 데이터 가져오기",
                $"{module} 모듈의 데이터를 {row.Program}의 데이터로 바꿉니다.\n\n" +
                $"원본: {rehearsal.SourceDatabase} (바꾸지 않음)\n" +
                $"대상: {target}\n" +
                $"대상에 있던 통합 앱의 데이터는 같은 폴더에 보관합니다(삭제하지 않음){(IncludeSettings && rehearsal.SettingsFile is not null ? ", 설정 파일도 함께 바꾸고 이전 설정은 보관" : "")}.\n" +
                $"{module} 모듈만 잠시 멈췄다가 다시 시작합니다. 다른 모듈은 계속 동작합니다.\n\n계속할까요?"))
        {
            return;
        }

        IsBusy = true;
        StatusText = $"{module}: 적용 중…";
        try
        {
            var result = await _migrator.ApplyAsync(rehearsal, _modules, IncludeSettings);
            _ui.Post(() =>
            {
                ShowChecks(result.Checks);
                row.State = result.Applied ? "가져옴" : "적용 실패 (이전 데이터로 되돌림)";
                row.Level = result.Applied ? Level.Good : Level.Error;
                StatusText = result.Message + (result.PreviousDatabaseKeptAt is { } kept ? $" 이전 데이터: {kept}" : "");
                if (result.Applied)
                {
                    row.Rehearsal = null;
                }
            });
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void OpenMigrationFolder() => _dialogs.OpenFolder(_paths.MigrationDirectory);

    private void ShowChecks(IEnumerable<MigrationCheck> checks)
    {
        Checks.Clear();
        foreach (var check in checks)
        {
            Checks.Add(check);
        }
    }
}
