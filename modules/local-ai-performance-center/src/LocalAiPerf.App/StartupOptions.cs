namespace LocalAiPerf.App;

/// <summary>Command line: --data-dir DIR, --mock-sensors, --strata-url URL, --smoke-test, --smoke-report DIR, --no-auto-record.</summary>
public sealed record StartupOptions
{
    public string? DataDirectory { get; init; }
    public bool MockSensors { get; init; }
    public string? StrataUrl { get; init; }
    public bool SmokeTest { get; init; }
    public string? SmokeReportDirectory { get; init; }
    public bool NoAutoRecord { get; init; }

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
                "--strata-url" => options with { StrataUrl = Next() },
                "--smoke-test" => options with { SmokeTest = true },
                "--smoke-report" => options with { SmokeReportDirectory = Next() },
                "--no-auto-record" => options with { NoAutoRecord = true },
                _ => options,
            };
        }

        return options;
    }
}
