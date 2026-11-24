using System.Text;
using System.Text.Json;
using WindowsAiOperator.Automation.Windows;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Cli;

/// <summary>
/// wao — read-only diagnostics for Windows AI Operator. It lists windows and UI Automation trees, reports the database
/// state (including tasks left unfinished by a crash, without changing them) and makes backups. It never clicks or types.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "version" => Print(new { product = AppInfo.ProductName, version = AppInfo.Version }, args),
                "windows" => Windows(args),
                "tree" => Tree(args),
                "status" => Status(args),
                "history" => History(args),
                "backup" => Backup(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"오류: {Redactor.OneLine(ex.Message, 300)}");
            return 2;
        }
    }

    private static void Usage()
    {
        Console.WriteLine($"{AppInfo.ProductName} 진단 도구 (wao) {AppInfo.Version} — 읽기 전용, 화면을 조작하지 않습니다.");
        Console.WriteLine("  wao windows [--all] [--json]                 열린 창과 자동화 허용 여부");
        Console.WriteLine("  wao tree (--hwnd N | --process NAME) [--json] UI Automation 요소 트리");
        Console.WriteLine("  wao status [--data-dir DIR] [--json]          데이터베이스 상태·무결성·미완료 작업(변경하지 않음)");
        Console.WriteLine("  wao history [--data-dir DIR] [--limit N] [--json]");
        Console.WriteLine("  wao backup [--data-dir DIR]                   검사된 백업 만들기");
        Console.WriteLine("  wao version");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"알 수 없는 명령: {command}");
        Usage();
        return 1;
    }

    private static int Windows(string[] args)
    {
        using var desktop = new FlaUiDesktop();
        var settings = LoadSettings(args);
        var policy = new PolicyEngine();
        var rows = desktop.ListWindows()
            .Select(w => (Window: w, Access: policy.CheckWindow(w, settings.Safety, desktop.OwnProcessId)))
            .Where(r => Has(args, "--all") || r.Access.CanRead)
            .Select(r => new
            {
                handle = r.Window.Handle,
                process = r.Window.ProcessName,
                pid = r.Window.ProcessId,
                title = Redactor.OneLine(r.Window.Title, 120),
                bounds = r.Window.Bounds.ToString(),
                dpi = r.Window.Dpi,
                scalePercent = (int)Math.Round(r.Window.Scale * 100),
                foreground = r.Window.IsForeground,
                access = r.Access.CanInteract ? "interact" : r.Access.CanRead ? "read-only" : "blocked",
                reason = r.Access.Reason,
            })
            .ToList();
        if (Has(args, "--json"))
        {
            return Print(rows, args);
        }

        foreach (var row in rows)
        {
            Console.WriteLine($"{row.handle,10} {row.access,-9} {row.process}.exe pid={row.pid} dpi={row.dpi}({row.scalePercent}%) {row.bounds} \"{row.title}\"");
        }

        Console.WriteLine(Has(args, "--all") ? $"창 {rows.Count}개" : $"허용 앱 창 {rows.Count}개 (--all: 모든 창)");
        return 0;
    }

    private static int Tree(string[] args)
    {
        using var desktop = new FlaUiDesktop();
        var settings = LoadSettings(args);
        WindowInfo? window = null;
        if (Value(args, "--hwnd") is { } hwndText && long.TryParse(hwndText, out var hwnd))
        {
            window = desktop.GetWindow(hwnd);
        }
        else if (Value(args, "--process") is { } process)
        {
            window = desktop.ListWindows().FirstOrDefault(w => string.Equals(w.ProcessName, AppSettings.NormalizeProcessName(process), StringComparison.OrdinalIgnoreCase));
        }

        if (window is null)
        {
            Console.Error.WriteLine("창을 찾지 못했습니다(--hwnd 또는 --process).");
            return 1;
        }

        var access = new PolicyEngine().CheckWindow(window, settings.Safety, desktop.OwnProcessId);
        if (!access.CanRead)
        {
            Console.Error.WriteLine("안전 정책상 이 창은 읽지 않습니다: " + access.Reason);
            return 3;
        }

        var snapshot = desktop.CaptureTree(window.Handle, new TreeCaptureOptions { MaxNodes = int.TryParse(Value(args, "--max"), out var max) ? max : 800 });
        if (Has(args, "--json"))
        {
            return Print(new
            {
                window = new { window.Handle, window.ProcessName, title = Redactor.OneLine(window.Title, 120), window.Dpi },
                status = snapshot.Status.ToString(),
                snapshot.StatusReason,
                snapshot.NodeCount,
                snapshot.Truncated,
                elements = snapshot.Flatten().Select(e => new
                {
                    e.Path,
                    e.ControlType,
                    name = Redactor.OneLine(e.Name, 120),
                    e.AutomationId,
                    patterns = e.Patterns.ToString(),
                    e.IsEnabled,
                    e.IsOffscreen,
                    bounds = e.Bounds.ToString(),
                    value = e.IsPassword ? "(password)" : e.Value is null ? null : Redactor.OneLine(e.Value, 120),
                }),
            }, args);
        }

        Console.WriteLine($"{window.Describe()} — {snapshot.Status} ({snapshot.NodeCount}개){(snapshot.StatusReason is null ? string.Empty : " " + snapshot.StatusReason)}");
        if (snapshot.Root is not null)
        {
            Write(snapshot.Root, 0);
        }

        return 0;

        static void Write(UiNode node, int depth)
        {
            var e = node.Element;
            Console.WriteLine($"{new string(' ', depth * 2)}{e.ControlType} \"{Redactor.OneLine(e.Name, 60)}\"{(e.AutomationId is null ? string.Empty : $" #{e.AutomationId}")} [{e.Patterns}]");
            foreach (var child in node.Children)
            {
                Write(child, depth + 1);
            }
        }
    }

    private static int Status(string[] args)
    {
        var paths = Paths(args);
        if (!File.Exists(paths.DatabasePath))
        {
            Console.Error.WriteLine("데이터베이스가 없습니다: " + paths.DatabasePath);
            return 1;
        }

        using var store = new OperatorStore(paths.DatabasePath);
        var status = store.GetStatus();
        var unfinished = status.UnfinishedTaskIds.Select(id => store.GetTask(id)).Where(d => d is not null).Select(d => new
        {
            d!.Summary.Id,
            d.Summary.Status,
            request = d.Summary.Request,
            steps = d.Steps.Select(s => new { s.Sequence, s.Action, s.Status, s.Performed }),
        }).ToList();
        return Print(new
        {
            database = paths.DatabasePath,
            integrity = status.Integrity,
            schema = status.SchemaVersion,
            readOnly = store.IsReadOnly,
            counts = new { status.Tasks, status.Steps, status.Events, status.Workflows, status.EvalRuns },
            unfinishedTasks = unfinished,
        }, args, forceJson: true);
    }

    private static int History(string[] args)
    {
        using var store = new OperatorStore(Paths(args).DatabasePath);
        var limit = int.TryParse(Value(args, "--limit"), out var n) ? n : 20;
        var rows = store.QueryTasks(new HistoryQuery(Limit: limit));
        if (Has(args, "--json"))
        {
            return Print(rows, args);
        }

        foreach (var row in rows)
        {
            Console.WriteLine($"{row.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} {row.Status,-18} 단계 {row.StepsSucceeded}/{row.StepCount} 변경 {row.ChangesPerformed} {row.Request}");
        }

        return 0;
    }

    private static int Backup(string[] args)
    {
        var paths = Paths(args);
        using var store = new OperatorStore(paths.DatabasePath);
        var backup = store.Backup(paths.BackupDirectory);
        Console.WriteLine($"백업 완료(검사 통과): {backup.Path} ({backup.SizeBytes} bytes)");
        return 0;
    }

    private static AppPaths Paths(string[] args) => AppPaths.Resolve(Value(args, "--data-dir"), AppContext.BaseDirectory);

    private static AppSettings LoadSettings(string[] args)
    {
        var paths = Paths(args);
        if (!File.Exists(paths.SettingsPath))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsPath), SettingsStore.JsonOptions) ?? AppSettings.CreateDefault();
            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            return AppSettings.CreateDefault();
        }
    }

    private static int Print(object value, string[] args, bool forceJson = false)
    {
        _ = forceJson || Has(args, "--json");
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        return 0;
    }

    private static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
