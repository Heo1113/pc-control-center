using GpuProfit.Accounting.Energy;
using GpuProfit.Core.Domain;

namespace GpuProfit.Tests.Accounting;

public class EnergyIntegratorTests
{
    private const double Tol = 1e-6;

    [Fact]
    public void Constant_power_for_one_hour_is_exact()
    {
        var samples = T.Constant(300, -1, 3601);
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 3600)], 10);
        Assert.Equal(300, e.MeasuredWh, 6);
        Assert.Equal(3600, e.ObservedSeconds, 6);
        Assert.Equal(1.0, e.Coverage!.Value, 9);
        Assert.Empty(e.Gaps);
        Assert.Equal(300, e.AveragePowerW!.Value, 6);
    }

    [Fact]
    public void Irregular_intervals_use_real_timestamps_trapezoid()
    {
        // 100 W at t=0, 200 W at t=3, 200 W at t=4, 50 W at t=10 (gaps 3, 1, 6 s; all under the 10 s limit).
        var samples = new List<PowerSample>
        {
            new(T.Ms(0), 0, 100), new(T.Ms(3), 0, 200), new(T.Ms(4), 0, 200), new(T.Ms(10), 0, 50),
        };
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 10)], 10);
        var expectedJoules = (100 + 200) / 2.0 * 3 + 200 * 1 + (200 + 50) / 2.0 * 6;
        Assert.Equal(expectedJoules / 3600, e.MeasuredWh, 9);
        Assert.Equal(10, e.ObservedSeconds, 9);
    }

    [Fact]
    public void Unordered_and_duplicate_samples_are_sorted_and_counted_once()
    {
        var samples = new List<PowerSample>
        {
            new(T.Ms(2), 0, 100), new(T.Ms(0), 0, 100), new(T.Ms(1), 0, 100), new(T.Ms(1), 0, 999),
        };
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 2)], 10);
        Assert.Equal(200.0 / 3600, e.MeasuredWh, 9);
    }

    [Fact]
    public void Gap_longer_than_limit_is_not_interpolated_and_is_reported()
    {
        // Samples until t=100, nothing until t=400 (app closed / PC asleep), then again.
        var samples = T.Constant(250, 0, 100).Concat(T.Constant(250, 400, 600)).ToList();
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 600)], 10);
        Assert.Equal(250 * 300 / 3600.0, e.MeasuredWh, 6);
        Assert.Equal(300, e.ObservedSeconds, 6);
        Assert.Equal(300, e.UnobservedSeconds, 6);
        var gap = Assert.Single(e.Gaps);
        Assert.Equal(GapReasons.SampleGap, gap.Reason);
        Assert.Equal(T.Ms(100), gap.FromMs);
        Assert.Equal(T.Ms(400), gap.ToMs);
        Assert.Equal(0.5, e.Coverage!.Value, 9);
    }

    [Fact]
    public void Gap_exactly_at_limit_is_still_observed()
    {
        var samples = new List<PowerSample> { new(T.Ms(0), 0, 100), new(T.Ms(10), 0, 100) };
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 10)], 10);
        Assert.Equal(1000.0 / 3600, e.MeasuredWh, 9);
        Assert.Empty(e.Gaps);
    }

    [Fact]
    public void Missing_power_values_break_the_integration()
    {
        // Sensor returned no power at t=5 and t=6 (e.g. GPU lost): intervals touching them are unobserved.
        var samples = new List<PowerSample>();
        for (var t = 0; t <= 10; t++)
        {
            samples.Add(new PowerSample(T.Ms(t), 0, t is 5 or 6 ? null : 100));
        }

        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 10)], 10);
        Assert.Equal(7, e.ObservedSeconds, 9); // [0,4] and [7,10]
        Assert.Equal(700.0 / 3600, e.MeasuredWh, 9);
        var gap = Assert.Single(e.Gaps);
        Assert.Equal(GapReasons.SensorMissing, gap.Reason);
        Assert.Equal(3, gap.Seconds, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    [InlineData(1e7)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_power_readings_are_not_counted(double bad)
    {
        var samples = new List<PowerSample> { new(T.Ms(0), 0, 100), new(T.Ms(1), 0, bad), new(T.Ms(2), 0, 100) };
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 2)], 10);
        Assert.Equal(0, e.MeasuredWh, 12);
        Assert.Equal(0, e.ObservedSeconds, 12);
        Assert.Equal(1, e.ValidPowerSamples); // window [0,2) holds t=0 (valid) and t=1 (invalid)
    }

    [Fact]
    public void Zero_watts_is_a_valid_measurement_not_a_gap()
    {
        var e = EnergyIntegrator.Integrate(0, T.Constant(0, 0, 60), [T.Window(0, 60)], 10);
        Assert.Equal(0, e.MeasuredWh);
        Assert.Equal(60, e.ObservedSeconds, 9);
        Assert.Equal(1.0, e.Coverage!.Value, 9);
        Assert.Empty(e.Gaps);
    }

    [Fact]
    public void Window_edges_are_clipped_with_interpolation_inside_the_interval()
    {
        // Ramp 0 W at t=0 to 100 W at t=10; window [2.5, 7.5] → average 50 W over 5 s.
        var samples = new List<PowerSample> { new(T.Ms(0), 0, 0), new(T.Ms(10), 0, 100) };
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(2.5, 7.5)], 10);
        Assert.Equal(50 * 5 / 3600.0, e.MeasuredWh, 9);
        Assert.Equal(5, e.ObservedSeconds, 9);
    }

    [Fact]
    public void Leading_and_trailing_time_without_samples_is_unobserved()
    {
        var e = EnergyIntegrator.Integrate(0, T.Constant(100, 5, 15), [T.Window(0, 20)], 10);
        Assert.Equal(10, e.ObservedSeconds, 9);
        Assert.Equal(2, e.Gaps.Count);
        Assert.Equal(GapReasons.NoSampleBefore, e.Gaps[0].Reason);
        Assert.Equal(GapReasons.NoSampleAfter, e.Gaps[1].Reason);
    }

    [Fact]
    public void No_samples_at_all_gives_zero_measured_and_full_gap()
    {
        var e = EnergyIntegrator.Integrate(0, [], [T.Window(0, 100)], 10);
        Assert.Equal(0, e.MeasuredWh);
        Assert.Equal(0, e.ObservedSeconds);
        Assert.Equal(100, e.UnobservedSeconds, 9);
        Assert.Equal(GapReasons.NoSamples, Assert.Single(e.Gaps).Reason);
        Assert.Null(e.AveragePowerW);
    }

    [Fact]
    public void Pauses_between_segments_are_not_counted()
    {
        var samples = T.Constant(200, 0, 300);
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 100), T.Window(200, 300)], 10);
        Assert.Equal(200 * 200 / 3600.0, e.MeasuredWh, 6);
        Assert.Equal(200, e.WindowSeconds, 9);
    }

    [Fact]
    public void Overlapping_windows_are_merged_and_not_double_counted()
    {
        var samples = T.Constant(100, 0, 100);
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 60), T.Window(40, 100)], 10);
        Assert.Equal(100 * 100 / 3600.0, e.MeasuredWh, 6);
        Assert.Equal(100, e.WindowSeconds, 9);
    }

    [Fact]
    public void Zero_length_window_gives_zero()
    {
        var e = EnergyIntegrator.Integrate(0, T.Constant(100, 0, 10), [T.Window(5, 5)], 10);
        Assert.Equal(0, e.MeasuredWh);
        Assert.Equal(0, e.WindowSeconds);
        Assert.Null(e.Coverage);
    }

    [Fact]
    public void Other_devices_are_ignored()
    {
        var samples = T.Constant(100, 0, 10, device: 0).Concat(T.Constant(400, 0, 10, device: 1)).ToList();
        Assert.Equal(1000.0 / 3600, EnergyIntegrator.Integrate(0, samples, [T.Window(0, 10)], 10).MeasuredWh, 9);
        Assert.Equal(4000.0 / 3600, EnergyIntegrator.Integrate(1, samples, [T.Window(0, 10)], 10).MeasuredWh, 9);
    }

    [Fact]
    public void Energy_counter_cross_check_and_reset_detection()
    {
        // 100 W for 10 s = 1000 J = 1,000,000 mJ; counter resets (driver reload) between t=5 and t=6.
        var samples = new List<PowerSample>();
        for (var t = 0; t <= 10; t++)
        {
            long counter = t <= 5 ? 50_000_000 + t * 100_000L : (t - 6) * 100_000L;
            samples.Add(new PowerSample(T.Ms(t), 0, 100, counter));
        }

        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 10)], 10);
        Assert.Equal(1, e.CounterResets);
        Assert.Equal(9, e.CounterObservedSeconds, 9);
        Assert.Equal(0.9 * 1000 / 3600, e.CounterWh!.Value, 9);
        Assert.Equal(1000.0 / 3600, e.MeasuredWh, 9);
    }

    [Fact]
    public void Gpu_activity_share_uses_the_utilization_threshold()
    {
        var samples = T.Constant(100, 0, 10, util: 5).Concat(T.Constant(100, 11, 20, util: 90)).ToList();
        var e = EnergyIntegrator.Integrate(0, samples, [T.Window(0, 20)], 10, activeUtilizationPercent: 10);
        Assert.Equal(20, e.UtilizationObservedSeconds, 9);
        Assert.Equal(10, e.GpuActiveSeconds, 9); // 10→11 averages 47.5 % (active); [0,10] at 5 % is not
    }

    [Fact]
    public void Power_statistics_use_only_valid_samples_inside_the_windows()
    {
        var samples = T.Constant(100, 0, 9).Concat(T.Constant(300, 10, 19)).Append(new PowerSample(T.Ms(5.5), 0, null)).Concat(T.Constant(999, 30, 40)).ToList();
        var stats = PowerStatistics.ForDevice(samples, [T.Window(0, 20)], 0);
        Assert.Equal(20, stats.Count);
        Assert.Equal(200, stats.Mean!.Value, 9);
        Assert.Equal(300, stats.P95!.Value, 9);
    }

    [Fact]
    public void Max_gap_must_be_positive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => EnergyIntegrator.Integrate(0, [], [T.Window(0, 1)], 0));

    [Fact]
    public void Large_input_is_handled_quickly()
    {
        // One week at 1 s with 200 pause segments.
        var samples = T.Constant(250, 0, 7 * 86400);
        var windows = Enumerable.Range(0, 200).Select(i => T.Window(i * 3000, i * 3000 + 2000)).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = EnergyIntegrator.Integrate(0, samples, windows, 10);
        Assert.Equal(250 * 400000 / 3600.0, e.MeasuredWh, 3);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}

public class SessionEnergyTests
{
    private static EnergyConditions C(EnergyBasis basis, double extra = 0, double idle = 0, UnobservedPolicy policy = UnobservedPolicy.Exclude, double? meter = null)
        => new() { Basis = basis, ExtraSystemPowerW = extra, IdleGpuPowerW = idle, MaxSampleGapSeconds = 10, UnobservedPolicy = policy, WallMeterKwh = meter };

    [Fact]
    public void Gpu_measured_basis_has_one_measured_component()
    {
        var r = SessionEnergyCalculator.Compute(T.Constant(300, 0, 3600), [T.Window(0, 3600)], null, C(EnergyBasis.GpuMeasured));
        Assert.Equal(300, r.TotalWh.Value!.Value, 6);
        Assert.Equal(1.0, r.MeasuredShare, 9);
        Assert.Equal(0.3, r.TotalKwh.Value!.Value, 9);
    }

    [Fact]
    public void System_assumption_is_added_as_an_estimated_component()
    {
        var r = SessionEnergyCalculator.Compute(T.Constant(300, 0, 3600), [T.Window(0, 3600)], null, C(EnergyBasis.GpuPlusSystemAssumption, extra: 100));
        Assert.Equal(400, r.TotalWh.Value!.Value, 6);
        var sys = Assert.Single(r.Components, c => c.Key == EnergyComponentKeys.SystemAssumption);
        Assert.Equal(ValueOrigin.Estimated, sys.Origin);
        Assert.Equal(0.25, r.EstimatedShare, 9);
    }

    [Fact]
    public void Incremental_basis_deducts_idle_power_but_never_below_zero_per_gpu()
    {
        var r = SessionEnergyCalculator.Compute(T.Constant(300, 0, 3600), [T.Window(0, 3600)], null, C(EnergyBasis.IncrementalGpu, idle: 30));
        Assert.Equal(270, r.TotalWh.Value!.Value, 6);

        var low = SessionEnergyCalculator.Compute(T.Constant(10, 0, 3600), [T.Window(0, 3600)], null, C(EnergyBasis.IncrementalGpu, idle: 30));
        Assert.Equal(0, low.TotalWh.Value!.Value, 6);
    }

    [Fact]
    public void Wall_meter_basis_uses_the_entered_value_and_ignores_gpu_samples()
    {
        var r = SessionEnergyCalculator.Compute(T.Constant(300, 0, 3600), [T.Window(0, 3600)], null, C(EnergyBasis.WallMeter, meter: 0.52));
        Assert.Equal(520, r.TotalWh.Value!.Value, 6);
        Assert.Equal(1.0, r.UserEnteredShare, 9);
        Assert.Equal(300, r.GpuMeasuredWh, 6); // still reported for reference
    }

    [Fact]
    public void Wall_meter_basis_without_a_reading_is_undefined()
    {
        var r = SessionEnergyCalculator.Compute(T.Constant(300, 0, 3600), [T.Window(0, 3600)], null, C(EnergyBasis.WallMeter));
        Assert.False(r.TotalWh.HasValue);
        Assert.Contains("전력계", r.TotalWh.Reason);
    }

    [Fact]
    public void No_gpu_samples_make_energy_undefined_not_zero()
    {
        var r = SessionEnergyCalculator.Compute([], [T.Window(0, 3600)], null, C(EnergyBasis.GpuPlusSystemAssumption, extra: 100));
        Assert.False(r.TotalWh.HasValue);
        Assert.Empty(r.Devices);
        Assert.Contains(r.Notes, n => n.Contains("관측 안 됨"));
    }

    [Fact]
    public void Requested_gpu_without_samples_is_reported_with_zero_coverage()
    {
        var r = SessionEnergyCalculator.Compute(T.Constant(300, 0, 100), [T.Window(0, 100)], [0, 1], C(EnergyBasis.GpuMeasured));
        Assert.Equal(2, r.Devices.Count);
        Assert.Equal(0, r.Coverage!.Value, 9);
        Assert.True(r.TotalWh.HasValue); // GPU 0 was observed; GPU 1 is reported as a gap
    }

    [Fact]
    public void Zero_hour_session_has_zero_energy_defined()
    {
        var r = SessionEnergyCalculator.Compute([], [], null, C(EnergyBasis.GpuMeasured));
        Assert.True(r.TotalWh.HasValue);
        Assert.Equal(0, r.TotalWh.Value!.Value);
    }

    [Fact]
    public void Multiple_gpus_are_summed_per_device()
    {
        var samples = T.Constant(300, 0, 3600, device: 0).Concat(T.Constant(150, 0, 3600, device: 1)).Concat(T.Constant(999, 0, 3600, device: 2)).ToList();
        var r = SessionEnergyCalculator.Compute(samples, [T.Window(0, 3600)], [0, 1], C(EnergyBasis.GpuMeasured));
        Assert.Equal(450, r.TotalWh.Value!.Value, 6);
        Assert.Equal([0, 1], r.Devices.Select(d => d.DeviceIndex));
    }

    [Fact]
    public void Unobserved_time_is_estimated_only_when_chosen_and_kept_separate()
    {
        var samples = T.Constant(200, 0, 1800).Concat(T.Constant(200, 2700, 3600)).ToList();
        var excluded = SessionEnergyCalculator.Compute(samples, [T.Window(0, 3600)], null, C(EnergyBasis.GpuMeasured));
        Assert.Equal(150, excluded.TotalWh.Value!.Value, 6);
        Assert.Contains(excluded.Notes, n => n.Contains("포함하지 않았습니다"));

        var estimated = SessionEnergyCalculator.Compute(samples, [T.Window(0, 3600)], null, C(EnergyBasis.GpuMeasured, policy: UnobservedPolicy.EstimateFromObservedAverage));
        Assert.Equal(200, estimated.TotalWh.Value!.Value, 6);
        var est = Assert.Single(estimated.Components, c => c.Key == EnergyComponentKeys.GpuUnobservedEstimate);
        Assert.Equal(ValueOrigin.Estimated, est.Origin);
        Assert.Equal(50, est.Wh, 6);
        Assert.Equal(150, estimated.GpuMeasuredWh, 6);
    }
}
