using Iron.App.Presentation;

namespace Iron.App.Tests;

/// <summary>A clock that moves only when told; scheduled actions run in order.</summary>
internal sealed class TestScheduler(DateTimeOffset start) : IUiScheduler
{
    private readonly List<(DateTimeOffset Due, Action Action, Token Token)> _queue = [];

    public DateTimeOffset Now { get; private set; } = start;

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var token = new Token();
        _queue.Add((Now + delay, action, token));
        return token;
    }

    public void Advance(TimeSpan by)
    {
        var until = Now + by;
        while (_queue.Where(q => !q.Token.Cancelled && q.Due <= until).OrderBy(q => q.Due).FirstOrDefault() is { Action: not null } next)
        {
            _queue.Remove(next);
            Now = next.Due;
            next.Action();
        }

        _queue.RemoveAll(q => q.Token.Cancelled);
        Now = until;
    }

    private sealed class Token : IDisposable
    {
        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}

internal static class Rig
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(9));

    public static (ShellViewModel Vm, TestScheduler Clock) Create(MockPreset preset = MockPreset.Normal)
    {
        var clock = new TestScheduler(T0);
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), clock);
        vm.Mock.Apply(preset);
        return (vm, clock);
    }

    /// <summary>Readings at 1 Hz.</summary>
    public static void Readings(this (ShellViewModel Vm, TestScheduler Clock) rig, int count)
    {
        for (var i = 0; i < count; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            rig.Vm.Tick();
        }
    }
}

public class AlertIndependenceTests
{
    [Fact]
    public void Gpu_temperature_warning_is_immediate_and_pin_warning_needs_three_readings()
    {
        var rig = Rig.Create(MockPreset.Alert);
        rig.Readings(1);

        Assert.True(rig.Vm.Gpus[0].IsHot);
        Assert.False(rig.Vm.Gpus[1].IsHot);
        Assert.False(rig.Vm.WireView.PinAlert);
        Assert.Equal(StatusRules.GpuText, rig.Vm.StatusText);
        Assert.Contains("1/3", rig.Vm.WireView.ConfirmText, StringComparison.Ordinal);

        rig.Readings(2);

        Assert.True(rig.Vm.WireView.PinAlert);
        Assert.Equal(StatusKind.Alert, rig.Vm.Status);
        Assert.Equal(StatusRules.BothText, rig.Vm.StatusText);
    }

    [Fact]
    public void Releasing_the_wireview_usb_clears_only_the_pin_warning_at_once()
    {
        var rig = Rig.Create(MockPreset.Alert);
        rig.Readings(3);
        Assert.Equal(StatusRules.BothText, rig.Vm.StatusText);

        rig.Vm.ToggleUsb(); // RELEASE USB: the reader stops now, the port closes a moment later

        Assert.Equal(PortState.Releasing, rig.Vm.WireView.Port);
        Assert.False(rig.Vm.WireView.PinAlert);
        Assert.All(rig.Vm.WireView.Pins, p => Assert.Equal(PinLevel.Inactive, p.Level));
        Assert.True(rig.Vm.Gpus[0].IsHot);
        Assert.Equal(StatusKind.Alert, rig.Vm.Status);
        Assert.Equal(StatusRules.GpuText, rig.Vm.StatusText);

        rig.Readings(3);

        Assert.Equal(PortState.Released, rig.Vm.WireView.Port);
        Assert.Equal(StatusRules.GpuText, rig.Vm.StatusText);
        Assert.True(rig.Vm.Gpus[0].IsHot);
    }

    [Fact]
    public void After_resume_the_pin_warning_needs_three_new_readings()
    {
        var rig = Rig.Create(MockPreset.Alert);
        rig.Readings(3);
        rig.Vm.ToggleUsb();
        rig.Readings(2);
        rig.Vm.ToggleUsb(); // RESUME
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PortState.Active, rig.Vm.WireView.Port);
        Assert.False(rig.Vm.WireView.PinAlert);

        rig.Readings(2);
        Assert.False(rig.Vm.WireView.PinAlert);
        rig.Readings(1);
        Assert.True(rig.Vm.WireView.PinAlert);
    }

    [Fact]
    public void Cooling_the_gpu_does_not_touch_the_pin_warning()
    {
        var rig = Rig.Create(MockPreset.Alert);
        rig.Readings(3);

        rig.Vm.Mock.GpuOverheat = false;
        rig.Readings(1);

        Assert.False(rig.Vm.Gpus[0].IsHot);
        Assert.True(rig.Vm.WireView.PinAlert);
        Assert.Equal(StatusRules.PinText, rig.Vm.StatusText);
    }

    [Fact]
    public void A_one_and_a_half_second_spike_flashes_the_pin_but_never_latches_the_warning()
    {
        var rig = Rig.Create(MockPreset.Normal);
        rig.Readings(2);

        rig.Vm.FlashImbalance();
        Assert.Equal(PinLevel.Over, rig.Vm.WireView.Pins[2].Level); // the measured value is shown as it is
        Assert.Equal(StatusKind.Online, rig.Vm.Status);

        for (var i = 0; i < 4; i++)
        {
            rig.Readings(1);
            Assert.False(rig.Vm.WireView.PinAlert);
            Assert.Equal(StatusKind.Online, rig.Vm.Status);
        }

        Assert.Equal(PinLevel.Normal, rig.Vm.WireView.Pins[2].Level);
    }

    [Fact]
    public void Status_priority_is_loading_then_disconnected_then_warnings_then_online()
    {
        Assert.Equal(StatusKind.Loading, StatusRules.For(SensorPhase.Initializing, true, true).Kind);
        Assert.Equal(StatusKind.Disconnected, StatusRules.For(SensorPhase.NoDevice, true, true).Kind);
        Assert.Equal(StatusRules.BothText, StatusRules.For(SensorPhase.Ready, true, true).Text);
        Assert.Equal(StatusRules.PinText, StatusRules.For(SensorPhase.Ready, false, true).Text);
        Assert.Equal(StatusRules.GpuText, StatusRules.For(SensorPhase.Ready, true, false).Text);
        Assert.Equal(StatusRules.OnlineText, StatusRules.For(SensorPhase.Ready, false, false).Text);
    }

    [Theory]
    [InlineData(MockPreset.Disconnected, StatusKind.Disconnected, "미연결")]
    [InlineData(MockPreset.Loading, StatusKind.Loading, "센서 초기화 중...")]
    [InlineData(MockPreset.Normal, StatusKind.Online, "ONLINE")]
    public void Presets_give_the_four_states(MockPreset preset, StatusKind kind, string text)
    {
        var rig = Rig.Create(preset);
        rig.Readings(3);

        Assert.Equal(kind, rig.Vm.Status);
        Assert.Equal(text, rig.Vm.StatusText);
    }
}

public class ThresholdTests
{
    [Theory]
    [InlineData("72", true)]
    [InlineData("55", true)]
    [InlineData("95", true)]
    [InlineData("54", false)]
    [InlineData("96", false)]
    [InlineData("72.5", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData("NaN", false)]
    [InlineData("1e2", false)]
    public void Gpu_temperature_threshold_accepts_whole_degrees_from_55_to_95(string text, bool valid)
    {
        var rig = Rig.Create();
        rig.Vm.GpuTemperatureLimit.Text = text;

        Assert.Equal(!valid, rig.Vm.GpuTemperatureLimit.HasError);
        Assert.Equal(valid ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 72, rig.Vm.GpuTemperatureLimit.Value);
    }

    [Theory]
    [InlineData("7.0", true, 7.0)]
    [InlineData("4", true, 4.0)]
    [InlineData("9.0", true, 9.0)]
    [InlineData("3.9", false, 7.0)]
    [InlineData("9.1", false, 7.0)]
    [InlineData("7.05", false, 7.0)]
    [InlineData("-7", false, 7.0)]
    public void Pin_threshold_accepts_four_to_nine_amperes_with_one_decimal(string text, bool valid, double expected)
    {
        var rig = Rig.Create();
        rig.Vm.PinCurrentLimit.Text = text;

        Assert.Equal(!valid, rig.Vm.PinCurrentLimit.HasError);
        Assert.Equal(expected, rig.Vm.PinCurrentLimit.Value);
    }

    [Fact]
    public void A_valid_threshold_recolors_at_once_and_an_invalid_one_changes_nothing()
    {
        var rig = Rig.Create(MockPreset.Alert); // RTX 5080 at 76 °C
        rig.Readings(1);
        Assert.True(rig.Vm.Gpus[0].IsHot);

        rig.Vm.GpuTemperatureLimit.Text = "80";
        Assert.False(rig.Vm.Gpus[0].IsHot); // no new reading needed

        rig.Vm.GpuTemperatureLimit.Text = "30"; // out of range: refused, 80 stays in effect
        Assert.True(rig.Vm.GpuTemperatureLimit.HasError);
        Assert.False(rig.Vm.Gpus[0].IsHot);
        Assert.Equal(80, rig.Vm.GpuTemperatureLimit.Value);

        rig.Vm.GpuTemperatureLimit.ResetToDefault();
        Assert.False(rig.Vm.GpuTemperatureLimit.HasError);
        Assert.True(rig.Vm.Gpus[0].IsHot);
    }

    [Fact]
    public void Raising_the_pin_threshold_above_the_currents_clears_the_pin_warning_at_once()
    {
        var rig = Rig.Create(MockPreset.Alert); // P3 7.94 A
        rig.Readings(3);
        Assert.True(rig.Vm.WireView.PinAlert);

        rig.Vm.PinCurrentLimit.Text = "8.5";

        Assert.False(rig.Vm.WireView.PinAlert);
        Assert.Equal(PinLevel.Approaching, rig.Vm.WireView.Pins[2].Level);
        Assert.True(rig.Vm.Gpus[0].IsHot); // the GPU warning is untouched
    }
}

public class PowerAndLayoutTests
{
    [Fact]
    public void One_gpu_without_power_gives_partial_total_and_no_cost()
    {
        var rig = Rig.Create(MockPreset.Alert);
        rig.Readings(1);

        Assert.True(rig.Vm.TotalPowerPartial);
        Assert.Equal("일부 미확인", rig.Vm.TotalPowerText);
        Assert.Equal("미합산", rig.Vm.CostPerHourText);
        Assert.Equal("일부 미확인", rig.Vm.Board.Value);
        Assert.True(rig.Vm.Board.IsWarning);
        Assert.Equal("N/A", rig.Vm.Gpus[1].PowerText);
    }

    [Fact]
    public void Both_gpus_with_power_give_the_sum_and_the_hourly_estimate()
    {
        var rig = Rig.Create();
        rig.Readings(1);

        Assert.Equal("126 W", rig.Vm.TotalPowerText); // 112 + 14
        Assert.Equal("23 ₩/h", rig.Vm.CostPerHourText); // 0.126 kWh × 185
        rig.Vm.Tariff.Text = "200";
        Assert.Equal("25 ₩/h", rig.Vm.CostPerHourText);
    }

    [Theory]
    [InlineData(2194, LayoutMode.Full, 2, 4, true)]
    [InlineData(1200, LayoutMode.Full, 2, 4, true)]
    [InlineData(1097, LayoutMode.Compact, 1, 4, true)]
    [InlineData(960, LayoutMode.Compact, 1, 4, true)]
    [InlineData(900, LayoutMode.Narrow, 1, 2, false)]
    public void Layout_follows_the_window_width(double width, LayoutMode mode, int gpuColumns, int systemColumns, bool sideBySide)
    {
        Assert.Equal(mode, LayoutRules.For(width));
        Assert.Equal(gpuColumns, LayoutRules.GpuColumns(mode));
        Assert.Equal(systemColumns, LayoutRules.SystemColumns(mode));
        Assert.Equal(sideBySide, LayoutRules.WorkAreaSideBySide(mode));
    }

    [Theory]
    [InlineData(LayoutMode.Full)]
    [InlineData(LayoutMode.Compact)]
    [InlineData(LayoutMode.Narrow)]
    public void Four_system_modules_always_fill_whole_rows(LayoutMode mode)
    {
        var columns = LayoutRules.SystemColumns(mode);
        Assert.Equal(0, 4 % columns); // 4 or 2×2, never 3 + 1
    }
}

public class DeviceTests
{
    [Fact]
    public void Flux_packet_follows_the_documented_format()
    {
        var packet = FluxPacket.Build(54.3, 48.2);

        Assert.Equal(12, packet.Length);
        Assert.Equal(new byte[] { 0x55, 0xAA, 0x01, 0x01, 0x06, 5, 4, 3, 4, 8, 2 }, packet[..11]);
        Assert.Equal((byte)(packet[..11].Sum(b => b) & 0xFF), packet[11]);
    }

    [Fact]
    public void Flux_shows_in_the_tray_only_when_disconnected_or_in_conflict()
    {
        var rig = Rig.Create();
        rig.Readings(1);
        Assert.Null(rig.Vm.Flux.TrayWarning);

        rig.Vm.Mock.IUnityRunning = true;
        Assert.Equal(FluxState.Conflict, rig.Vm.Flux.State);
        Assert.NotNull(rig.Vm.Flux.TrayWarning);

        rig.Vm.Mock.IUnityRunning = false;
        rig.Vm.Mock.FluxDisconnected = true;
        Assert.Equal(FluxState.Disconnected, rig.Vm.Flux.State);
        Assert.Equal("미연결", rig.Vm.Flux.TrayWarning);
    }

    [Fact]
    public void Flux_auto_target_shows_the_hotter_gpu()
    {
        var gpus = new[]
        {
            new GpuReading { Index = 0, Name = "RTX 5080", TemperatureC = 48 },
            new GpuReading { Index = 1, Name = "RTX 5060 Ti", TemperatureC = 61 },
        };

        var (celsius, label) = FluxViewModel.TargetTemperature(gpus, FluxViewModel.GpuTargets[2]);

        Assert.Equal(61, celsius);
        Assert.Contains("5060 Ti", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_while_another_program_holds_the_port_retries_every_three_seconds()
    {
        var rig = Rig.Create();
        rig.Readings(1);
        rig.Vm.Mock.WireViewPortBusy = true;
        rig.Vm.ToggleUsb();
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PortState.Released, rig.Vm.WireView.Port);

        rig.Vm.ToggleUsb();
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PortState.Busy, rig.Vm.WireView.Port);

        rig.Vm.Mock.WireViewPortBusy = false;
        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(PortState.Active, rig.Vm.WireView.Port);
    }

    [Fact]
    public void Processes_hide_the_software_adapter_by_default_and_filter_by_gpu()
    {
        var rig = Rig.Create();
        rig.Readings(1);
        var p = rig.Vm.Processes;

        Assert.DoesNotContain(p.Rows, r => r.IsSoftwareAdapter);
        Assert.Equal("basalt-server.exe", p.Rows[0].Name); // largest dedicated VRAM first

        p.ShowSoftwareAdapter = true;
        Assert.Contains(p.Rows, r => r.IsSoftwareAdapter);

        p.Filter = "RTX 5060 Ti";
        Assert.All(p.Rows, r => Assert.Equal("RTX 5060 Ti", r.Adapter));
        Assert.All(p.OverviewRows, r => Assert.Equal("RTX 5060 Ti", r.Adapter));

        p.Filter = ProcessListViewModel.All;
        p.Search = "dwm";
        Assert.Single(p.Rows);
    }

    [Fact]
    public void Command_line_options_are_parsed_and_bad_ones_reported()
    {
        var o = AppOptions.Parse(["--tab", "power", "--layout", "half", "--mock-state", "alert", "--usb-released", "--tray"]);
        Assert.Empty(o.Errors);
        Assert.Equal(IronTab.Power, o.Tab);
        Assert.Equal(WindowLayout.Half, o.Layout);
        Assert.Equal(MockPreset.Alert, o.Preset);
        Assert.True(o.UsbReleased);
        Assert.True(o.StartInTray);

        Assert.NotEmpty(AppOptions.Parse(["--layout", "quarter"]).Errors);
        Assert.NotEmpty(AppOptions.Parse(["--bogus"]).Errors);
        Assert.Equal([(2194.0, 1186.0), (1097.0, 1186.0)], AppOptions.Parse(["--render-screens", "x"]).RenderSizes);
    }
}

public class NarrowAndOverviewTests
{
    [Fact]
    public void Narrow_windows_shorten_labels_but_keep_the_full_status_in_the_tooltip_text()
    {
        var rig = Rig.Create(MockPreset.Alert);
        rig.Readings(3);

        rig.Vm.Layout = LayoutMode.Narrow;
        Assert.Equal("전력", rig.Vm.PowerTabLabel);
        Assert.Equal("장치", rig.Vm.DevicesTabLabel);
        Assert.Equal("핀 · 온도 주의", rig.Vm.StatusDisplayText);
        Assert.Equal(StatusRules.BothText, rig.Vm.StatusText);

        rig.Vm.Layout = LayoutMode.Compact;
        Assert.Equal("전력 · WireView", rig.Vm.PowerTabLabel);
        Assert.Equal(StatusRules.BothText, rig.Vm.StatusDisplayText);
    }

    [Fact]
    public void The_overview_table_holds_exactly_the_rows_that_fit()
    {
        var rig = Rig.Create();
        rig.Readings(1);
        var p = rig.Vm.Processes;

        p.SetOverviewLimit(4);
        Assert.Equal(4, p.OverviewRows.Count);
        p.SetOverviewLimit(12);
        Assert.Equal(12, p.OverviewRows.Count);
        p.SetOverviewLimit(0);
        Assert.Single(p.OverviewRows);
        Assert.DoesNotContain(p.OverviewRows, r => r.IsSoftwareAdapter);
    }

    [Fact]
    public void Selection_made_in_one_list_survives_the_other_list_reporting_nothing_selected()
    {
        var rig = Rig.Create();
        rig.Readings(1);
        var p = rig.Vm.Processes;
        p.SetOverviewLimit(3);
        var low = p.Rows.Last();

        p.ListSelection = low;      // chosen in the full table
        p.ListSelection = null;     // the overview list does not contain it and reports "nothing"

        Assert.Same(low, p.Selected);
        Assert.Contains(low, p.Rows);
    }

    [Fact]
    public void Percentages_never_show_minus_zero()
    {
        Assert.Equal("0", Fmt.Int(-0.4));
        Assert.Equal("00%", Fmt.Percent2(-0.2));
    }
}
