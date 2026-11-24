using LocalAiPerf.Core.Hardware;
using Suite.Core.Hardware;

namespace Suite.Tests;

public class GpuPowerTotalTests
{
    [Fact]
    public void Dual_gpu_total_is_the_sum_when_both_report_power()
    {
        var total = GpuPowerTotal.From(Gpus.Snapshot(Gpus.Rtx5080(312.4), Gpus.Rtx5060Ti(151.8)), expectedDevices: 2);

        Assert.Equal(PowerTotalState.Complete, total.State);
        Assert.Equal(464.2, total.TotalW!.Value, 6);
        Assert.Equal(2, total.Reported);
        Assert.Equal(2, total.Expected);
        Assert.Equal(MetricSource.Nvml, total.Source);
        Assert.Equal(Gpus.T0, total.MeasuredAt);
    }

    [Theory]
    [InlineData(null, 151.8)]
    [InlineData(312.4, null)]
    public void One_gpu_without_power_makes_the_total_partial_and_never_a_smaller_sum(double? first, double? second)
    {
        var total = GpuPowerTotal.From(Gpus.Snapshot(Gpus.Rtx5080(first), Gpus.Rtx5060Ti(second)), expectedDevices: 2);

        Assert.Equal(PowerTotalState.Partial, total.State);
        Assert.Null(total.TotalW);
        Assert.Equal(1, total.Reported);
        Assert.Equal(2, total.Expected);
        Assert.Null(total.EstimatedCostPerHour(200m));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5.0)]
    public void Invalid_power_values_count_as_missing(double bad)
    {
        var total = GpuPowerTotal.From(Gpus.Snapshot(Gpus.Rtx5080(bad), Gpus.Rtx5060Ti()), expectedDevices: 2);

        Assert.Equal(PowerTotalState.Partial, total.State);
        Assert.Null(total.TotalW);
    }

    [Fact]
    public void A_gpu_missing_from_the_reading_still_counts_as_expected()
    {
        // The reader knows two GPUs, the reading lists only one (the other dropped off the bus): PARTIAL, not 312 W.
        var total = GpuPowerTotal.From(Gpus.Snapshot(Gpus.Rtx5080()), expectedDevices: 2);

        Assert.Equal(PowerTotalState.Partial, total.State);
        Assert.Null(total.TotalW);
        Assert.Equal(2, total.Expected);
    }

    [Fact]
    public void No_reading_no_gpu_or_no_power_is_unavailable()
    {
        Assert.Equal(PowerTotalState.Unavailable, GpuPowerTotal.From(null, 2).State);
        Assert.Equal(PowerTotalState.Unavailable, GpuPowerTotal.From(Gpus.Snapshot(), 0).State);
        // A stale placeholder (no values) for a dual GPU system.
        var stale = GpuPowerTotal.From(Gpus.Snapshot(), 2);
        Assert.Equal(PowerTotalState.Unavailable, stale.State);
        Assert.Equal(0, stale.Reported);
        Assert.Null(stale.TotalW);
        var none = GpuPowerTotal.From(Gpus.Snapshot(Gpus.Rtx5080(null), Gpus.Rtx5060Ti(null)), 2);
        Assert.Equal(PowerTotalState.Unavailable, none.State);
        Assert.Null(none.EstimatedCostPerHour(200m));
    }

    [Fact]
    public void Cost_per_hour_needs_a_complete_total_and_a_rate()
    {
        var total = GpuPowerTotal.From(Gpus.Snapshot(Gpus.Rtx5080(300), Gpus.Rtx5060Ti(200)), 2);

        Assert.Equal(100m, total.EstimatedCostPerHour(200m)); // 0.5 kWh × 200 원
        Assert.Null(total.EstimatedCostPerHour(null));
        Assert.Null(total.EstimatedCostPerHour(-1m));
    }

    [Fact]
    public void Mock_readings_are_labelled_as_mock()
    {
        var snapshot = Gpus.Snapshot(Gpus.Rtx5080(), Gpus.Rtx5060Ti()) with { IsMock = true };

        Assert.Equal(MetricSource.Mock, GpuPowerTotal.From(snapshot, 2).Source);
    }
}

public class SourcedValueTests
{
    [Fact]
    public void Values_of_one_source_are_summed()
    {
        var sum = SourcedValues.SumOfOneSource(
        [
            new Sourced<double>(300, MetricSource.Nvml, Gpus.T0),
            new Sourced<double>(150, MetricSource.Nvml, Gpus.T0.AddSeconds(1)),
        ]);

        Assert.Equal(450, sum!.Value.Value);
        Assert.Equal(MetricSource.Nvml, sum.Value.Source);
        Assert.Equal(Gpus.T0, sum.Value.MeasuredAt);
    }

    [Theory]
    [InlineData(MetricSource.WddmPdh)]
    [InlineData(MetricSource.WireView)]
    public void Values_of_different_sources_are_never_summed(MetricSource other)
    {
        Assert.Throws<InvalidOperationException>(() => SourcedValues.SumOfOneSource(
        [
            new Sourced<double>(14, MetricSource.Nvml, Gpus.T0),
            new Sourced<double>(13, other, Gpus.T0),
        ]));
    }

    [Fact]
    public void No_values_is_no_sum_not_zero() => Assert.Null(SourcedValues.SumOfOneSource([]));
}

public class GpuDeviceKeyTests
{
    [Fact]
    public void Uuid_comes_first_then_pci_location_then_index()
    {
        var pci = new GpuPciAddress(0, 0x01, 0x00, 0);

        Assert.Equal("uuid:GPU-1234", GpuDeviceKey.Of("GPU-1234", pci, 0));
        Assert.Equal("pci:0000:01:00.0", GpuDeviceKey.Of(null, pci, 0));
        Assert.Equal("pci:0000:0a:00.0", GpuDeviceKey.Of(" ", new GpuPciAddress(null, 0x0a, 0, 0), 1));
        Assert.Equal("index:1", GpuDeviceKey.Of(null, null, 1));
    }

    [Fact]
    public void Sample_and_device_info_give_the_same_key()
    {
        var sample = Gpus.Rtx5060Ti();
        var info = new GpuInfo(1, sample.Name, 16L << 30, 180) { DeviceUuid = sample.DeviceUuid };

        Assert.Equal(GpuDeviceKey.Of(info), GpuDeviceKey.Of(sample));
    }
}
