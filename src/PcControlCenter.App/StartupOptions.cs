namespace PcControlCenter.App;

/// <summary>Command line of PcControlCenter.exe (same option names as the original programs where they overlap).</summary>
internal sealed record StartupOptions
{
    public string? DataDirectory { get; init; }
    public bool MockSensors { get; init; }
    public bool DemoData { get; init; }

    /// <summary>Operator: E's simulated desktop in the host process (nothing on the real screen is touched).</summary>
    public bool MockDesktop { get; init; }

    /// <summary>Operator: E's MOCK model profile (no model server needed; fixed example requests only).</summary>
    public bool MockModel { get; init; }
    public bool SmokeTest { get; init; }
    public string? SmokeReportDirectory { get; init; }
    public string? StartSection { get; init; }

    /// <summary>Smoke test only: an original program's data folder to rehearse and import through the data page.</summary>
    public string? SmokeMigrateFrom { get; init; }

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
                "--mock-desktop" => options with { MockDesktop = true },
                "--mock-model" => options with { MockModel = true },
                "--smoke-test" => options with { SmokeTest = true },
                "--smoke-report" => options with { SmokeReportDirectory = Next() },
                "--section" => options with { StartSection = Next() },
                "--smoke-migrate-from" => options with { SmokeMigrateFrom = Next() },
                _ => options,
            };
        }

        return options;
    }
}
