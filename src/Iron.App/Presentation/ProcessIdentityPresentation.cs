using Iron.ProcessIdentity;

namespace Iron.App.Presentation;

/// <summary>What the existing process-name tooltip shows: the identity text and, when known, the program's icon.</summary>
public sealed record ProcessIdentityView(string Text, IconPixels? Icon, IdentitySource Source);

/// <summary>Gives the identity of a process row.</summary>
public interface IProcessIdentityProvider
{
    ProcessIdentityView Identify(ProcessReading reading);
}

/// <summary>
/// Stage 1: the rows are mock processes, so their identity comes only from the mock name and mock command line through
/// the same rules (Iron.ProcessIdentity), with no system call at all — a mock PID is never looked up in the real process
/// table. Connecting real processes (the native resolver of Iron.ProcessIdentity) belongs to Stage 2 and needs its own approval.
/// </summary>
public sealed class MockProcessIdentityProvider : IProcessIdentityProvider
{
    private readonly Dictionary<(int Pid, string Name, string CommandLine), ProcessIdentityView> _cache = [];

    public const int Capacity = 256;

    public ProcessIdentityView Identify(ProcessReading reading)
    {
        var key = (reading.Pid, reading.Name, reading.CommandLine);
        if (_cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        if (_cache.Count >= Capacity)
        {
            _cache.Clear();
        }

        var info = IdentityComposer.ComposeMock(reading.Pid, reading.Name, reading.CommandLine);
        return _cache[key] = new ProcessIdentityView(info.ToToolTipText(), info.Icon, info.Source);
    }
}
