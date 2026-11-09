namespace WinDiag.App;

/// <summary>
/// Command line: --data-dir DIR, --mock-sensors, --demo-data (load the virtual scenario into the data folder),
/// --smoke-test, --smoke-report DIR, --history-days N.
/// </summary>
public sealed record StartupOptions
{
    public string? DataDirectory { get; init; }
    public bool MockSensors { get; init; }
    public bool DemoData { get; init; }
    public bool SmokeTest { get; init; }
    public string? SmokeReportDirectory { get; init; }
    public int? HistoryDays { get; init; }

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
                "--history-days" => options with { HistoryDays = int.TryParse(Next(), out var days) ? days : null },
                _ => options,
            };
        }

        return options;
    }
}
