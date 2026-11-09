// Compatibility shim for project A's HardwareMonitor.Create(SamplingSettings, ...) (vendored unchanged).
// Project A defines this type in LocalAiPerf.Core/Settings/AppSettings.cs together with its Strata settings; this app
// needs only the two switches HardwareMonitor reads, so only they are declared here (same names, same defaults).
// Same approach as project C (WinDiag.Collection/Sensors/SamplingSettingsShim.cs, commit 06cc9e5).
namespace LocalAiPerf.Core.Settings;

public sealed class SamplingSettings
{
    public bool GpuEnabled { get; set; } = true;
    public bool SystemEnabled { get; set; } = true;
}
