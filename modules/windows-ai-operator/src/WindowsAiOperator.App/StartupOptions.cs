namespace WindowsAiOperator.App;

/// <summary>
/// Command line: --data-dir DIR, --mock-desktop (simulated desktop, nothing real is touched), --mock-model (test
/// planner), --smoke-test, --smoke-report DIR, --real-uia-smoke (the smoke test also drives the real test app),
/// --start-test-task (starts a long mock task right away; used to test recovery after the app is killed).
/// Same shape as the user's project B StartupOptions.
/// </summary>
public sealed record StartupOptions
{
    public string? DataDirectory { get; init; }
    public bool MockDesktop { get; init; }
    public bool MockModel { get; init; }
    public bool SmokeTest { get; init; }
    public string? SmokeReportDirectory { get; init; }
    public bool RealUiaSmoke { get; init; }
    public bool StartTestTask { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            options = args[i] switch
            {
                "--data-dir" => options with { DataDirectory = Next() },
                "--mock-desktop" => options with { MockDesktop = true },
                "--mock-model" => options with { MockModel = true },
                "--smoke-test" => options with { SmokeTest = true },
                "--smoke-report" => options with { SmokeReportDirectory = Next() },
                "--real-uia-smoke" => options with { RealUiaSmoke = true },
                "--start-test-task" => options with { StartTestTask = true, MockDesktop = true },
                _ => options,
            };
        }

        return options;
    }
}
