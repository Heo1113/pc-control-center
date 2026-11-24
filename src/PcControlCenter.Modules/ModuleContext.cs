using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using PcControlCenter.Hardware;

namespace PcControlCenter.Modules;

/// <summary>What every module adapter gets from the shell.</summary>
public sealed class ModuleContext
{
    public required ShellPaths Paths { get; init; }
    public required ShellSettings Settings { get; init; }
    public required SharedHardwareHub Hub { get; init; }
    public required SensorIntervalCoordinator Intervals { get; init; }

    /// <summary>The shell's own log (module engines log to their own files in their folder, as before).</summary>
    public IAppLogger Log { get; init; } = NullLogger.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Folder of PcControlCenter.exe (the modules' AppPaths get an explicit data folder and ignore it).</summary>
    public string ExecutableDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Loads the labelled demo data of B and C into their databases (tests, screenshots). Never real values.</summary>
    public bool DemoData { get; init; }

    /// <summary>Do not save module settings files created with defaults (smoke tests).</summary>
    public bool Unattended { get; init; }

    public bool IsMock => Hub.IsMock;
}
