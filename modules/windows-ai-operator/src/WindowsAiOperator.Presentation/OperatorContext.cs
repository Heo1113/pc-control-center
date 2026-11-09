using System.Collections.Concurrent;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Core.Workflows;
using WindowsAiOperator.ModelProviders;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation;

/// <summary>Everything the app works with: data folder, settings, database, desktop, secrets, log and model state.</summary>
public sealed class OperatorContext : IDisposable
{
    private readonly Lock _settingsGate = new();

    public OperatorContext(AppPaths paths, IDesktopAutomation desktop, ISecretStore secrets, IOperatorLog log, bool forceMockModel = false)
    {
        Paths = paths;
        paths.EnsureDirectories();
        Desktop = desktop;
        Secrets = secrets;
        Log = log;
        SettingsStore = new SettingsStore(paths.SettingsPath);
        var (settings, notes) = SettingsStore.Load();
        Settings = settings;
        if (forceMockModel && !settings.ModelProfiles.Any(p => p.Kind == ProviderKind.Mock))
        {
            settings.ModelProfiles.Insert(0, new ModelProfile { Id = "mock", Name = "MOCK 모델(테스트용)", Kind = ProviderKind.Mock, BaseUrl = "mock://local", Model = "mock-planner" });
        }

        if (forceMockModel)
        {
            settings.ActiveModelProfileId = settings.ModelProfiles.First(p => p.Kind == ProviderKind.Mock).Id;
        }

        Store = new OperatorStore(paths.DatabasePath);
        Recovered = Store.RecoverInterruptedTasks();
        var startupNotes = new List<string>(notes);
        if (paths.Note is not null)
        {
            startupNotes.Add(paths.Note);
        }

        if (Store.IsReadOnly)
        {
            startupNotes.Add("데이터베이스가 이 프로그램보다 새 버전에서 만들어져 기록을 저장하지 않습니다.");
        }

        if (Recovered.Count > 0)
        {
            startupNotes.Add($"지난번 실행이 작업 도중 끝나 작업 {Recovered.Count}개를 '중단/부분 완료'로 정리했습니다. 자동으로 다시 실행하지 않습니다.");
        }

        StartupNotes = startupNotes;
        Models = new ModelConnectionManager(this);
        Workflows = new WorkflowService(Store);
        Workflows.SeedBuiltIns();
        Tasks = new TaskService(this);
        TryAutoBackup();
        TryApplyRetention();
        TryCleanScreenshots();
        log.Info("app", $"started v{AppInfo.Version}, desktop={desktop.Name}, data={paths.DataDirectory}, recovered={Recovered.Count}");
    }

    public AppPaths Paths { get; }
    public IDesktopAutomation Desktop { get; }
    public ISecretStore Secrets { get; }
    public IOperatorLog Log { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public OperatorStore Store { get; }
    public PolicyEngine Policy { get; } = new();
    public IReadOnlyList<RecoveredTask> Recovered { get; }
    public IReadOnlyList<string> StartupNotes { get; }
    public ModelConnectionManager Models { get; }
    public WorkflowService Workflows { get; }
    public TaskService Tasks { get; }

    public event Action? SettingsChanged;

    public IReadOnlyList<string> SaveSettings()
    {
        IReadOnlyList<string> notes;
        lock (_settingsGate)
        {
            notes = Settings.Normalize();
            SettingsStore.Save(Settings);
        }

        SettingsChanged?.Invoke();
        return notes;
    }

    public void Dispose()
    {
        Store.Dispose();
        Desktop.Dispose();
    }

    private void TryAutoBackup()
    {
        if (Settings.General.AutoBackupDays <= 0 || Store.IsReadOnly)
        {
            return;
        }

        try
        {
            var newest = OperatorStore.ListBackups(Paths.BackupDirectory).FirstOrDefault();
            if (newest is null || DateTimeOffset.Now - newest.CreatedAt > TimeSpan.FromDays(Settings.General.AutoBackupDays))
            {
                Store.Backup(Paths.BackupDirectory);
                OperatorStore.PruneBackups(Paths.BackupDirectory, 10);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Warn("backup", "automatic backup failed", ex);
        }
    }

    private void TryCleanScreenshots()
    {
        try
        {
            if (!Directory.Exists(Paths.ScreenshotDirectory))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-Settings.General.ScreenshotRetentionDays);
            foreach (var folder in Directory.EnumerateDirectories(Paths.ScreenshotDirectory))
            {
                if (Directory.GetLastWriteTime(folder) < cutoff)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("screenshots", "old screenshots could not be removed", ex);
        }
    }

    private void TryApplyRetention()
    {
        try
        {
            Store.ApplyRetention(Settings.General.HistoryRetentionDays);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Warn("store", "retention failed", ex);
        }
    }
}

/// <summary>Connection checks and model clients per profile. Results are kept for the session only.</summary>
public sealed class ModelConnectionManager(OperatorContext context)
{
    private readonly ConcurrentDictionary<string, ProbeResult> _probes = new();
    private readonly ConcurrentDictionary<string, ResponseFormatKind> _formats = new();

    public event Action? Changed;

    public ProbeResult? LastProbe(ModelProfile profile) => _probes.GetValueOrDefault(profile.Id);

    public async Task<ProbeResult> ProbeAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Kind == ProviderKind.Mock)
        {
            var mock = new ProbeResult
            {
                Reachable = true,
                Message = "MOCK 모델(테스트용, 실제 AI 아님)",
                ServerKind = "MOCK",
                Models = [new ProbeModel("mock-planner", false, "정해진 예시 요청만 처리")],
                SelectedModelAcceptsImages = false,
                VisionReason = "MOCK 모델은 이미지를 읽지 않습니다.",
            };
            _probes[profile.Id] = mock;
            Changed?.Invoke();
            return mock;
        }

        var (key, _) = ApiKeyResolver.Resolve(profile, context.Secrets);
        var result = await ModelProbe.ProbeAsync(profile, key, null, cancellationToken).ConfigureAwait(false);
        _probes[profile.Id] = result;
        context.Log.Info("model", $"probe {profile.Name}: reachable={result.Reachable} kind={result.ServerKind} models={result.Models.Count} vision={result.SelectedModelAcceptsImages}");
        Changed?.Invoke();
        return result;
    }

    public (IModelClient? Client, string? Problem) CreateClient(ModelProfile profile)
    {
        var (client, problem) = ModelClientFactory.Create(profile, context.Secrets, LastProbe(profile), null, context.Log,
            _formats.TryGetValue(profile.Id, out var format) ? format : null);
        return (client, problem);
    }

    /// <summary>Remembers which structured-output format the server accepted, so the next task starts with it.</summary>
    public void Remember(ModelProfile profile, IModelClient client)
    {
        if (client is OpenAiCompatibleClient { EffectiveFormat: { } format })
        {
            _formats[profile.Id] = format;
        }
    }

    public (bool CanSend, string Reason) Vision(ModelProfile profile)
    {
        if (profile.Kind == ProviderKind.Mock)
        {
            return (false, "MOCK 모델은 이미지를 읽지 않습니다.");
        }

        return ModelClientFactory.DecideVision(profile, LastProbe(profile), EndpointPolicy.Check(profile));
    }

    public async Task<VisionTestResult> TestVisionAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var (client, problem) = ModelClientFactory.CreateForVisionTest(profile, context.Secrets);
        if (client is null)
        {
            return new VisionTestResult(false, false, problem ?? "이미지 테스트를 할 수 없습니다.", TimeSpan.Zero);
        }

        using (client as IDisposable)
        {
            var result = await VisionTester.RunAsync(client, cancellationToken).ConfigureAwait(false);
            profile.VisionVerified = result.Passed;
            profile.VisionVerifiedModel = result.Passed ? profile.Model : null;
            context.SaveSettings();
            Changed?.Invoke();
            return result;
        }
    }

    public async Task<PlanFormatTestResult> TestPlanFormatAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var (client, problem) = CreateClient(profile);
        if (client is null)
        {
            return new PlanFormatTestResult(false, false, problem ?? "모델에 연결할 수 없습니다.", TimeSpan.Zero, 0, null);
        }

        try
        {
            var result = await PlanFormatTester.RunAsync(client, cancellationToken).ConfigureAwait(false);
            Remember(profile, client);
            return result;
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }
}

/// <summary>Workflow records in the database; the four examples are added once and can be copied, not deleted.</summary>
public sealed class WorkflowService(OperatorStore store)
{
    public void SeedBuiltIns()
    {
        if (store.IsReadOnly)
        {
            return;
        }

        var existing = store.ListWorkflows().Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, workflow) in BuiltInWorkflows.All)
        {
            if (!existing.Contains(id))
            {
                store.SaveWorkflow(new WorkflowRecord(id, workflow.Name, workflow.Description, workflow.TargetApp, WorkflowSerializer.Serialize(workflow), true, DateTimeOffset.Now, DateTimeOffset.Now));
            }
        }
    }

    public IReadOnlyList<WorkflowRecord> List() => store.ListWorkflows();

    public WorkflowRecord Save(WorkflowDefinition workflow, string? id = null)
    {
        var record = new WorkflowRecord(id ?? "wf-" + Guid.NewGuid().ToString("N")[..12], workflow.Name, workflow.Description, workflow.TargetApp,
            WorkflowSerializer.Serialize(workflow), false, DateTimeOffset.Now, DateTimeOffset.Now);
        store.SaveWorkflow(record);
        return record;
    }

    public bool Delete(WorkflowRecord record) => !record.BuiltIn && store.DeleteWorkflow(record.Id);
}

/// <summary>Starts tasks: a natural-language request through the AI planner, or a workflow without any model.</summary>
public sealed class TaskService(OperatorContext context)
{
    public ExecutorOptions ExecutorOptions { get; set; } = new();

    public (IPlanSource? Source, ModelProfile? Profile, IModelClient? Client, string? Problem) CreatePlanner()
    {
        var profile = context.Settings.ActiveProfile;
        if (profile is null)
        {
            return (null, null, null, "선택된 모델 연결이 없습니다. 모델 화면에서 연결을 고르세요.");
        }

        var (client, problem) = context.Models.CreateClient(profile);
        if (client is null)
        {
            return (null, profile, null, problem);
        }

        var visionAllowed = context.Settings.Safety.AllowVision && profile.Vision == VisionPreference.UseWhenSupported;
        return (new ModelPlanner(client, visionAllowed, context.Log), profile, client, null);
    }

    public async Task<TaskRun> RunAsync(TaskRun run, IPlanSource source, IApprovalHandler approvals, ITaskObserver observer, ExecutionControl control, CancellationToken cancellationToken)
    {
        var options = context.Settings.General.SaveScreenshots
            ? ExecutorOptions with { ScreenshotSink = SaveScreenshot }
            : ExecutorOptions with { ScreenshotSink = null };
        var executor = new TaskExecutor(context.Desktop, context.Policy, approvals,
            new CompositeTaskObserver(new TaskJournal(context.Store, context.Log), observer), context.Log, options);
        return await executor.RunAsync(run, source, context.Settings.Safety, control, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a vision screenshot under screenshots\&lt;task id&gt;\ (only when the user turned this on in Settings).</summary>
    private void SaveScreenshot(TaskRun run, ScreenImage image)
    {
        try
        {
            var folder = Path.Combine(context.Paths.ScreenshotDirectory, run.Id.ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"{DateTime.Now:HHmmss-fff}-window-{image.WindowHandle}.png"), image.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Log.Warn("screenshots", "saving a screenshot failed", ex);
        }
    }

    /// <summary>A single step chosen by the user in the inspector, through the same policy and approval path.</summary>
    public static TaskPlan SingleStep(PlanStep step) => new() { Summary = step.Summary(), Steps = [step] };
}
