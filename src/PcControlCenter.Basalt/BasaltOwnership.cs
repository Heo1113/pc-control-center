using System.Text.Json;
using PcControlCenter.Core;

namespace PcControlCenter.Basalt;

/// <summary>
/// The processes this app started for Basalt: the PowerShell window it opened and everything verified to run under it
/// (wrapper, Python server, engine, an engine started again by them). Only these are ever stopped. Kept in a small file in
/// the app's data folder (PIDs, start times, the mode; no paths, no command lines), so that after the app is closed and
/// opened again it still stops the Basalt it started, and never one started elsewhere.
/// </summary>
public sealed class BasaltOwnership
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string? _file;
    private readonly HashSet<(int Id, long Ticks)> _owned = [];

    /// <param name="file">Where to keep the record; null keeps it in memory only (tests).</param>
    public BasaltOwnership(string? file)
    {
        _file = file;
        Load();
    }

    public BasaltLaunch? Launch { get; private set; }

    /// <summary>The record came from the file (the app was restarted with Basalt started by an earlier run).</summary>
    public bool Restored { get; private set; }

    public bool IsEmpty => Launch is null && _owned.Count == 0;

    public void Started(BasaltLaunch launch)
    {
        Launch = launch;
        Restored = false;
        _owned.Clear();
        if (launch.StartTime is { } start)
        {
            _owned.Add((launch.ProcessId, start.UtcTicks));
        }

        Save();
    }

    public bool Owns(BasaltProcess process) => process.StartTime is { } start && _owned.Contains((process.Id, start.UtcTicks));

    /// <summary>
    /// The processes of <paramref name="candidates"/> that are this app's: owned already, or (fixed point) a child that
    /// started after an owned parent. Nothing is owned unless something already is (the window this app opened).
    /// </summary>
    public IReadOnlyList<BasaltProcess> Ours(IEnumerable<BasaltProcess> candidates, IReadOnlyList<BasaltProcess> all)
    {
        var ours = new Dictionary<int, BasaltProcess>();
        foreach (var p in all.Where(Owns))
        {
            ours[p.Id] = p;
        }

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var p in all)
            {
                if (ours.ContainsKey(p.Id) || p.StartTime is not { } start || p.ParentId == p.Id)
                {
                    continue;
                }

                if (ours.TryGetValue(p.ParentId, out var parent) && parent.StartTime is { } ps && start >= ps)
                {
                    ours[p.Id] = p;
                    changed = true;
                }
            }
        }

        return [.. candidates.Where(c => ours.TryGetValue(c.Id, out var o) && o.StartTime == c.StartTime)];
    }

    /// <summary>Remembers these processes as this app's (they were found under an owned one). Returns whether anything was new.</summary>
    public bool Adopt(IEnumerable<BasaltProcess> processes)
    {
        var added = false;
        foreach (var p in processes)
        {
            if (p.StartTime is { } start)
            {
                added |= _owned.Add((p.Id, start.UtcTicks));
            }
        }

        if (added)
        {
            Save();
        }

        return added;
    }

    /// <summary>Forgets owned processes that no longer run (same PID and start time).</summary>
    public void Prune(IReadOnlyList<BasaltProcess> now)
    {
        var alive = now.Where(p => p.StartTime is not null).Select(p => (p.Id, p.StartTime!.Value.UtcTicks)).ToHashSet();
        var unreadable = now.Where(p => p.StartTime is null).Select(p => p.Id).ToHashSet();
        if (_owned.RemoveWhere(o => !alive.Contains(o) && !unreadable.Contains(o.Id)) > 0)
        {
            Save();
        }
    }

    public bool MayBeAlive(IReadOnlyList<BasaltProcess> now) => now.Any(p => Owns(p) || (p.StartTime is null && _owned.Any(k => k.Id == p.Id)));

    public void Clear()
    {
        Launch = null;
        Restored = false;
        _owned.Clear();
        if (_file is not null)
        {
            try
            {
                File.Delete(_file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Save()
    {
        if (_file is null || Launch is null)
        {
            return;
        }

        try
        {
            var record = new Record(Launch.ProcessId, Launch.StartTime?.UtcTicks, Launch.Mode, Launch.StartedAt.UtcTicks,
                [.. _owned.Select(o => new Owned(o.Id, o.Ticks))]);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the memory across restarts is lost; this run still knows what it started.
        }
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(_file));
            if (record is null || record.LauncherId <= 4 || record.Owned is null || !BasaltSettings.Modes.Contains(record.Mode ?? ""))
            {
                return;
            }

            Launch = new BasaltLaunch(record.LauncherId, record.LauncherStartTicks is long t ? new DateTimeOffset(t, TimeSpan.Zero) : null, record.Mode!,
                new DateTimeOffset(record.StartedAtTicks, TimeSpan.Zero));
            foreach (var o in record.Owned.Where(o => o.Id > 4 && o.StartTicks > 0))
            {
                _owned.Add((o.Id, o.StartTicks));
            }

            Restored = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // An unreadable record owns nothing: nothing is stopped because of it.
        }
    }

    private sealed record Record(int LauncherId, long? LauncherStartTicks, string? Mode, long StartedAtTicks, List<Owned>? Owned);

    private sealed record Owned(int Id, long StartTicks);
}
