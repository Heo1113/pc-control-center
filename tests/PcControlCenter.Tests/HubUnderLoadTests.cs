using System.Diagnostics;
using LocalAiPerf.Telemetry;
using PcControlCenter.Hardware;

namespace PcControlCenter.Tests;

/// <summary>Runs alone: it deliberately blocks the whole thread pool for two seconds.</summary>
[CollectionDefinition(nameof(ThreadPoolSaturation), DisableParallelization = true)]
public sealed class ThreadPoolSaturation;

[Collection(nameof(ThreadPoolSaturation))]
public class HubUnderLoadTests
{
    [Fact(Timeout = 60_000)]
    public async Task Readings_stay_on_time_while_the_thread_pool_is_blocked()
    {
        // Module recorders park pool threads while they wait for the next reading, and startup work (event log reads,
        // databases) blocks more. Before the reader had its own thread, its first reading came 5-6 s late in exactly this
        // situation on a 4-core machine, so consumers recorded "not measured" placeholders (seen on the Windows CI runner).
        TimeSpan first = default, third = default;
        long readings = 0;
        var measure = new Thread(() =>
        {
            using var release = new ManualResetEventSlim(false);
            for (var i = 0; i < 4 * Environment.ProcessorCount; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(TimeSpan.FromSeconds(2)), null);
            }

            Thread.Sleep(200);
            var hub = new SharedHardwareHub(new HardwareMonitor(new CountingGpuProvider(TimeProvider.System), new CountingSystemProvider()),
                TimeSpan.FromMilliseconds(500));
            var clock = Stopwatch.StartNew();
            hub.Start();
            SpinWait.SpinUntil(() => hub.Latest is not null, TimeSpan.FromSeconds(10));
            first = clock.Elapsed;
            SpinWait.SpinUntil(() => hub.Health.Readings >= 3, TimeSpan.FromSeconds(10));
            third = clock.Elapsed;
            readings = hub.Health.Readings;
            release.Set();
            hub.StopAsync().GetAwaiter().GetResult();
        })
        { IsBackground = true };
        measure.Start();
        await Task.Run(measure.Join, TestContext.Current.CancellationToken);

        Assert.True(first < TimeSpan.FromMilliseconds(500), $"first reading after {first.TotalMilliseconds:0} ms");
        Assert.True(readings >= 3 && third < TimeSpan.FromMilliseconds(1600), $"third reading after {third.TotalMilliseconds:0} ms");
    }
}
