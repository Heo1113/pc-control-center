using WinDiag.Core.Events;

namespace WinDiag.Collection.Sources;

/// <summary>
/// An event source held in memory: imported XML files, the virtual demo scenario and tests. It can simulate the
/// situations a real log goes through (access denied, missing channel, wrapped or cleared log, read failures).
/// </summary>
public sealed class MemoryEventSource(string name, bool isVirtual) : IEventLogSource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<RawEvent>> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ChannelAccess Access, string? Message)> _access = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _nextRecordId = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;
    public bool IsVirtual { get; } = isVirtual;

    public IReadOnlyList<string> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels.Keys];
            }
        }
    }

    /// <summary>Number of Read calls (tests).</summary>
    public int ReadCalls { get; private set; }

    /// <summary>When set, the next Read throws this exception after yielding this many events (tests: crash mid-read).</summary>
    public (int AfterEvents, Exception Error)? FailNextRead { get; set; }

    /// <summary>Adds an event. Its record ID is assigned when it is 0, otherwise kept (must increase per channel).</summary>
    public RawEvent Add(RawEvent e)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(e.Channel, out var list))
            {
                list = [];
                _channels[e.Channel] = list;
            }

            var next = _nextRecordId.GetValueOrDefault(e.Channel, 1);
            var stored = e.RecordId > 0 ? e : e with { RecordId = next };
            _nextRecordId[e.Channel] = Math.Max(next, stored.RecordId + 1);
            list.Add(stored);
            list.Sort((a, b) => a.RecordId.CompareTo(b.RecordId));
            return stored;
        }
    }

    public void EnsureChannel(string channel)
    {
        lock (_gate)
        {
            _channels.TryAdd(channel, []);
        }
    }

    public void SetAccess(string channel, ChannelAccess access, string? message = null)
    {
        lock (_gate)
        {
            _access[channel] = (access, message);
        }
    }

    /// <summary>Simulates a full log overwriting its oldest records.</summary>
    public void DropOldest(string channel, int count)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(channel, out var list))
            {
                list.RemoveRange(0, Math.Min(count, list.Count));
            }
        }
    }

    /// <summary>Simulates a cleared log; with <paramref name="resetRecordIds"/> numbering restarts at 1 (log recreated).</summary>
    public void Clear(string channel, bool resetRecordIds)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(channel, out var list))
            {
                list.Clear();
            }

            if (resetRecordIds)
            {
                _nextRecordId[channel] = 1;
            }
        }
    }

    public ChannelProbe Probe(string channel)
    {
        lock (_gate)
        {
            if (_access.TryGetValue(channel, out var access) && access.Access != ChannelAccess.Ok)
            {
                return new ChannelProbe(channel, access.Access, access.Message);
            }

            if (!_channels.TryGetValue(channel, out var list))
            {
                return new ChannelProbe(channel, ChannelAccess.NotFound, "채널이 없습니다.");
            }

            return list.Count == 0
                ? new ChannelProbe(channel, ChannelAccess.Ok, null, RecordCount: 0)
                : new ChannelProbe(channel, ChannelAccess.Ok, null, list[0].RecordId, list[0].TimeCreated, list[^1].RecordId, list[^1].TimeCreated, list.Count);
        }
    }

    public IEnumerable<RawEvent> Read(EventReadRequest request, CancellationToken cancellationToken)
    {
        List<RawEvent> matching;
        (int AfterEvents, Exception Error)? failure;
        lock (_gate)
        {
            ReadCalls++;
            if (_access.TryGetValue(request.Channel, out var access))
            {
                if (access.Access == ChannelAccess.AccessDenied)
                {
                    throw new UnauthorizedAccessException(access.Message ?? "액세스가 거부되었습니다.");
                }

                if (access.Access != ChannelAccess.Ok)
                {
                    throw new EventSourceException(access.Message ?? "채널을 읽을 수 없습니다.");
                }
            }

            matching = (_channels.GetValueOrDefault(request.Channel) ?? [])
                .Where(e => request.AfterRecordId is not long after || e.RecordId > after)
                .Where(e => request.From is not { } from || e.TimeCreated >= from)
                .Where(e => request.To is not { } to || e.TimeCreated <= to)
                .Where(request.Filter.Matches)
                .ToList();
            failure = FailNextRead;
            FailNextRead = null;
        }

        for (var i = 0; i < matching.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is { } f && i == f.AfterEvents)
            {
                throw f.Error;
            }

            yield return matching[i];
        }

        if (failure is { } late && late.AfterEvents >= matching.Count)
        {
            throw late.Error;
        }
    }

    /// <summary>Loads events from Windows event XML files (for example "wevtutil qe System /f:xml" output).</summary>
    public static MemoryEventSource FromXmlFiles(IEnumerable<string> paths, string? channelOverride = null)
    {
        var source = new MemoryEventSource("XML 파일 가져오기", isVirtual: false);
        foreach (var path in paths)
        {
            using var reader = new StreamReader(path);
            foreach (var e in EventXmlParser.ParseMany(reader, channelOverride))
            {
                source.Add(e);
            }
        }

        return source;
    }
}
