using CommunityToolkit.Mvvm.ComponentModel;

namespace Iron.App.Presentation;

/// <summary>One slim graphite GPU card: model, temperature · power · clock, GPU load and VRAM (NVML only).</summary>
public sealed partial class GpuCardViewModel(int index) : ObservableObject
{
    public string IndexText { get; } = (index + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string TemperatureText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial double? TemperatureC { get; private set; }

    /// <summary>At or above the GPU temperature UI threshold: only this card's temperature and load bar turn red.</summary>
    [ObservableProperty] public partial bool IsHot { get; private set; }

    [ObservableProperty] public partial string PowerText { get; private set; } = "N/A";
    [ObservableProperty] public partial bool PowerMissing { get; private set; }
    [ObservableProperty] public partial string ClockText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string LoadText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial double? LoadRatio { get; private set; }
    [ObservableProperty] public partial string VramText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string VramTotalText { get; private set; } = "/ 16 GB";
    [ObservableProperty] public partial double? VramRatio { get; private set; }
    [ObservableProperty] public partial string VramShortText { get; private set; } = Fmt.Missing;

    public void Apply(GpuReading gpu, bool hot, bool measuring)
    {
        Name = gpu.Name;
        TemperatureC = gpu.TemperatureC;
        TemperatureText = Fmt.Celsius(gpu.TemperatureC);
        IsHot = hot;
        PowerMissing = measuring && gpu.PowerW is null;
        PowerText = measuring ? Fmt.Watts(gpu.PowerW) : Fmt.Missing;
        ClockText = Fmt.Mhz(gpu.ClockMhz);
        LoadText = Fmt.Int(gpu.UtilizationPercent);
        LoadRatio = gpu.UtilizationPercent / 100.0;
        VramText = Fmt.One(gpu.VramUsedGb);
        VramTotalText = $"/ {Fmt.Int(gpu.VramTotalGb)} GB";
        VramRatio = gpu.VramUsedGb is double used && gpu.VramTotalGb > 0 ? used / gpu.VramTotalGb : null;
        VramShortText = gpu.VramUsedGb is double v ? Fmt.One(v) + " GB" : Fmt.Missing;
    }
}

/// <summary>One stone module of the system row (CPU, system RAM, NVMe SSD, GPU board power).</summary>
public sealed partial class SystemTileViewModel(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty] public partial string Value { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string Sub { get; private set; } = "";
    [ObservableProperty] public partial double? Ratio { get; private set; }

    /// <summary>The value is not a number but a state ("일부 미확인"): drawn in the error ink, no meter.</summary>
    [ObservableProperty] public partial bool IsWarning { get; private set; }

    /// <summary>Null when there is nothing to explain (no empty tooltip box).</summary>
    [ObservableProperty] public partial string? ToolTip { get; private set; }

    public void Set(string value, string sub, double? ratio, bool warning = false, string? toolTip = null)
    {
        Value = value;
        Sub = sub;
        Ratio = ratio is double r ? Math.Clamp(r, 0, 1) : null;
        IsWarning = warning;
        ToolTip = string.IsNullOrEmpty(toolTip) ? null : toolTip;
    }
}

/// <summary>One 12 V pin of the WireView connector (P1..P6).</summary>
public sealed partial class PinViewModel(int index) : ObservableObject
{
    public string Label { get; } = $"P{index + 1}";

    [ObservableProperty] public partial double? CurrentA { get; private set; }
    [ObservableProperty] public partial string Text { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string ShortText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial PinLevel Level { get; private set; } = PinLevel.Inactive;

    public void Set(double? currentA, PinLevel level)
    {
        CurrentA = level == PinLevel.Inactive ? null : currentA;
        Text = Fmt.Two(CurrentA);
        ShortText = Fmt.One(CurrentA);
        Level = level;
    }
}

/// <summary>The last 60 readings of a value, oldest first (null = not measured: a gap in the line, never a 0).</summary>
public sealed class History(int capacity = 60)
{
    private readonly Queue<double?> _values = new();

    public int Capacity { get; } = capacity;

    public void Add(double? value)
    {
        _values.Enqueue(value);
        while (_values.Count > Capacity)
        {
            _values.Dequeue();
        }
    }

    public double?[] Snapshot() => [.. _values];

    public void Clear() => _values.Clear();
}
