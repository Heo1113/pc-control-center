using GpuProfit.Accounting.Energy;
using GpuProfit.Core.Domain;

namespace GpuProfit.Tests;

internal static class T
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static long Ms(double seconds) => T0.ToUnixTimeMilliseconds() + (long)Math.Round(seconds * 1000);

    public static DateTimeOffset At(double seconds) => T0.AddMilliseconds(Math.Round(seconds * 1000));

    /// <summary>Constant power samples every <paramref name="step"/> seconds from <paramref name="from"/> to <paramref name="to"/> inclusive.</summary>
    public static List<PowerSample> Constant(double watts, double from, double to, double step = 1, int device = 0, double? util = 100)
    {
        var list = new List<PowerSample>();
        for (var t = from; t <= to + 1e-9; t += step)
        {
            list.Add(new PowerSample(Ms(t), device, watts, null, util));
        }

        return list;
    }

    public static TimeWindow Window(double from, double to) => new(Ms(from), Ms(to));

    public static WorkSession Session(long id = 1, double from = 0, double to = 3600) => new()
    {
        Id = id,
        WorkloadId = 1,
        Started = At(from),
        Ended = At(to),
        State = SessionState.Ended,
    };

    public static SessionSegment Segment(double from, double? to, long sessionId = 1) => new()
    {
        SessionId = sessionId,
        Start = At(from),
        End = to is double t ? At(t) : null,
    };
}
