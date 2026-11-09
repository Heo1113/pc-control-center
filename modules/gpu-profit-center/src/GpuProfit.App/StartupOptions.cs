namespace GpuProfit.App;

/// <summary>
/// Command line: --data-dir DIR, --mock-sensors, --demo-data (add MOCK demo sessions), --smoke-test, --smoke-report DIR,
/// --start-test-session (testing the unclean-end recovery: starts recording a session right away).
/// Same shape as project C's StartupOptions (commit 06cc9e5).
/// </summary>
public sealed record StartupOptions
{
    public string? DataDirectory { get; init; }
    public bool MockSensors { get; init; }
    public bool DemoData { get; init; }
    public bool SmokeTest { get; init; }
    public string? SmokeReportDirectory { get; init; }
    public bool StartTestSession { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            options = args[i] switch
            {
                "--data-dir" => options with { DataDirectory = Next() },
                "--mock-sensors" => options with { MockSensors = true },
                "--demo-data" => options with { DemoData = true },
                "--smoke-test" => options with { SmokeTest = true },
                "--smoke-report" => options with { SmokeReportDirectory = Next() },
                "--start-test-session" => options with { StartTestSession = true },
                _ => options,
            };
        }

        return options;
    }
}
