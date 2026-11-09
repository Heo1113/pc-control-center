using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Runtimes.Strata;

namespace LocalAiPerf.Runtimes;

/// <summary>
/// Creates runtime adapters. Strata is implemented; other engines (Ollama, llama.cpp server) can be added
/// by implementing <see cref="IAiRuntimeAdapter"/> and registering them here.
/// </summary>
public static class RuntimeAdapterFactory
{
    public static IReadOnlyList<string> SupportedKinds { get; } = [StrataAdapter.RuntimeKindId];

    public static IAiRuntimeAdapter? Create(AppSettings settings, TimeProvider? time = null, IAppLogger? log = null)
        => settings.Strata.Enabled ? StrataAdapter.FromSettings(settings.Strata, time, log) : null;
}
