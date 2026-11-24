using System.Windows.Threading;
using Iron.App.Presentation;

namespace Iron.App.Hosting;

/// <summary>Delayed actions on the UI thread.</summary>
internal sealed class DispatcherScheduler(Dispatcher dispatcher) : IUiScheduler
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

/// <summary>A clock that only moves when told (offscreen renders: the same picture every run).</summary>
internal sealed class ManualScheduler(DateTimeOffset start) : IUiScheduler
{
    private readonly List<(DateTimeOffset Due, Action Action, Cancel Token)> _queue = [];

    public DateTimeOffset Now { get; private set; } = start;

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var token = new Cancel();
        _queue.Add((Now + delay, action, token));
        return token;
    }

    public void Advance(TimeSpan by)
    {
        var until = Now + by;
        while (true)
        {
            var next = _queue.Where(q => !q.Token.Cancelled && q.Due <= until).OrderBy(q => q.Due).FirstOrDefault();
            if (next.Action is null)
            {
                break;
            }

            _queue.Remove(next);
            Now = next.Due;
            next.Action();
        }

        _queue.RemoveAll(q => q.Token.Cancelled);
        Now = until;
    }

    private sealed class Cancel : IDisposable
    {
        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}
