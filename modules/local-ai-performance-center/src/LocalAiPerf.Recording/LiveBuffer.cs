using LocalAiPerf.Core;

namespace LocalAiPerf.Recording;

/// <summary>Recent frames kept in memory for the live charts. Independent of recording.</summary>
public sealed class LiveBuffer(TimeSpan window)
{
    private readonly object _gate = new();
    private readonly LinkedList<TelemetryFrame> _frames = new();
    private TimeSpan _window = window;

    public TimeSpan Window
    {
        get
        {
            lock (_gate)
            {
                return _window;
            }
        }
        set
        {
            lock (_gate)
            {
                _window = value;
                Trim();
            }
        }
    }

    public TelemetryFrame? Latest
    {
        get
        {
            lock (_gate)
            {
                return _frames.Last?.Value;
            }
        }
    }

    public void Add(TelemetryFrame frame)
    {
        lock (_gate)
        {
            _frames.AddLast(frame);
            Trim();
        }
    }

    public IReadOnlyList<TelemetryFrame> Snapshot()
    {
        lock (_gate)
        {
            return [.. _frames];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
        }
    }

    private void Trim()
    {
        if (_frames.Last is null)
        {
            return;
        }

        var cutoff = _frames.Last.Value.Timestamp - _window;
        while (_frames.First is { } first && first.Value.Timestamp < cutoff)
        {
            _frames.RemoveFirst();
        }
    }
}
