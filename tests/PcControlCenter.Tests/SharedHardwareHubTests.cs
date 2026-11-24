using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Telemetry;
using PcControlCenter.Hardware;

namespace PcControlCenter.Tests;

public class SharedHardwareHubTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static (SharedHardwareHub Hub, CountingGpuProvider Gpu, CountingSystemProvider System, ManualTime Time) NewHub(TimeSpan? consumerWait = null)
    {
        var time = new ManualTime(T0);
        var gpu = new CountingGpuProvider(time);
        var system = new CountingSystemProvider();
        var monitor = new HardwareMonitor(gpu, system, time);
        var hub = new SharedHardwareHub(monitor, TimeSpan.FromSeconds(1), time, consumerWait: consumerWait ?? TimeSpan.FromMilliseconds(50));
        return (hub, gpu, system, time);
    }

    [Fact]
    public void Three_consumers_share_one_device_read_per_reading()
    {
        var (hub, gpu, _, time) = NewHub();
        var a = hub.CreateConsumer();
        var b = hub.CreateConsumer();
        var c = hub.CreateConsumer();
        for (var i = 0; i < 5; i++)
        {
            var reading = hub.ReadNow();
            Assert.Same(reading.Snapshot, a.Sample());
            Assert.Same(reading.Snapshot, b.Sample());
            Assert.Same(reading.Snapshot, c.Sample());
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(5, gpu.Reads);
        Assert.Equal(5, hub.Health.Readings);
        Assert.Equal(3, hub.Health.Consumers);
    }

    [Fact]
    public void Consumers_get_the_original_timestamp_and_issues()
    {
        var (hub, gpu, _, time) = NewHub();
        var consumer = hub.CreateConsumer();
        gpu.Issue = new SensorIssue("counting-gpu", "gpu0", SensorStatus.Error, "fan sensor failed");
        var reading = hub.ReadNow();
        time.Advance(TimeSpan.FromMilliseconds(900));

        var seen = consumer.Sample();

        Assert.Equal(T0, seen.Timestamp);
        Assert.Contains(seen.Issues, i => i.Message == "fan sensor failed");
        Assert.Equal(reading.Snapshot.Gpus[0].PowerW, seen.Gpus[0].PowerW);
    }

    [Fact]
    public async Task A_reading_is_never_handed_out_twice_to_the_same_consumer()
    {
        var (hub, _, _, time) = NewHub();
        hub.Start();
        try
        {
            // Start() takes the first reading on the loop thread; wait for it (real time, bounded).
            SpinWait.SpinUntil(() => hub.Latest is not null, TimeSpan.FromSeconds(5));
            var consumer = hub.CreateConsumer();
            var first = consumer.Sample();
            time.Advance(TimeSpan.FromSeconds(3));

            // The manual clock does not drive the loop's timer, so no new reading arrives: stale placeholder, no values.
            var second = consumer.Sample();

            Assert.NotEmpty(first.Gpus);
            Assert.Empty(second.Gpus);
            Assert.Null(second.System.CpuUtilizationPercent);
            Assert.Equal(T0 + TimeSpan.FromSeconds(3), second.Timestamp);
            Assert.Contains(second.Issues, i => i.Scope == "hub" && i.Status == SensorStatus.Unavailable);
        }
        finally
        {
            await hub.StopAsync();
        }
    }

    [Fact]
    public void A_slower_consumer_gets_the_newest_reading_not_an_old_one()
    {
        var (hub, _, _, time) = NewHub();
        var consumer = hub.CreateConsumer();
        hub.ReadNow();
        time.Advance(TimeSpan.FromSeconds(1));
        hub.ReadNow();
        time.Advance(TimeSpan.FromSeconds(1));
        var newest = hub.ReadNow();

        Assert.Same(newest.Snapshot, consumer.Sample());
    }

    [Fact]
    public async Task Stopped_hub_gives_stale_placeholders_and_says_why()
    {
        var (hub, _, _, _) = NewHub(TimeSpan.FromSeconds(30));
        await hub.StopAsync();
        var consumer = hub.CreateConsumer();

        var started = DateTime.UtcNow;
        var sample = consumer.Sample();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "a stopped hub must not make consumers wait");
        Assert.Empty(sample.Gpus);
        Assert.Contains(sample.Issues, i => i.Message.Contains("중지", StringComparison.Ordinal));
    }

    [Fact]
    public void Views_report_the_real_providers_and_never_read_the_device()
    {
        var (hub, gpu, system, _) = NewHub();
        var systemReadsBefore = system.Reads;
        var view = hub.CreateConsumer();

        Assert.True(view.IsSharedView);
        Assert.True(view.Gpu.IsAvailable);
        Assert.Equal("counting-gpu", view.Gpu.Name);
        Assert.Equal("test-driver", view.Info.GpuDriverVersion);
        Assert.Contains(view.Capabilities, c => c.MetricKey == "gpu.power");
        Assert.Empty(view.Gpu.ReadGpus(new List<SensorIssue>()));
        view.Dispose();
        Assert.Equal(0, gpu.Reads);
        Assert.Equal(systemReadsBefore, system.Reads);

        // Disposing a view does not dispose the shared reader.
        hub.ReadNow();
        Assert.Equal(1, gpu.Reads);
    }

    [Fact]
    public void Mock_readings_stay_marked_as_mock()
    {
        var time = new ManualTime(T0);
        var hub = SharedHardwareHub.Create(gpuEnabled: true, systemEnabled: true, useMock: true, TimeSpan.FromSeconds(1), time);
        var view = hub.CreateConsumer();
        hub.ReadNow();

        var sample = view.Sample();

        Assert.True(hub.IsMock);
        Assert.True(view.IsMock);
        Assert.True(sample.IsMock);
        Assert.True(view.Info.IsMock);
    }

    [Fact]
    public void The_hub_rejects_a_shared_view_as_its_reader()
    {
        var (hub, _, _, _) = NewHub();
        Assert.Throws<ArgumentException>(() => new SharedHardwareHub(hub.CreateConsumer(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Live_buffer_keeps_raw_readings_with_their_timestamps()
    {
        var (hub, _, _, time) = NewHub();
        for (var i = 0; i < 4; i++)
        {
            hub.ReadNow();
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var live = hub.Live(TimeSpan.FromSeconds(2));

        Assert.Equal([T0.AddSeconds(1), T0.AddSeconds(2), T0.AddSeconds(3)], live.Select(s => s.Timestamp));
    }

    [Fact]
    public async Task Interval_is_clamped_to_the_range_of_project_A()
    {
        var (hub, _, _, _) = NewHub();
        await hub.SetIntervalAsync(TimeSpan.FromMilliseconds(10));
        Assert.Equal(SharedHardwareHub.MinInterval, hub.Interval);
        await hub.SetIntervalAsync(TimeSpan.FromHours(1));
        Assert.Equal(SharedHardwareHub.MaxInterval, hub.Interval);
    }
}
