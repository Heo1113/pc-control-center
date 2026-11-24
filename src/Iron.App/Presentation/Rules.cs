using System.Globalization;

namespace Iron.App.Presentation;

/// <summary>
/// The sum of the GPUs' board power (NVML) and the hourly cost estimate. With two GPUs (RTX 5080 + RTX 5060 Ti) the
/// total exists only when every GPU reported power: one missing value makes it "일부 미확인" and the cost "미합산",
/// never a smaller sum (the backend invariant; Stage 2 uses the shared library's implementation).
/// </summary>
public sealed record PowerTotal(double? TotalW, int Reported, int Expected)
{
    public bool IsComplete => TotalW is not null;
    public bool IsPartial => TotalW is null && Reported > 0;

    public static PowerTotal Of(IReadOnlyList<GpuReading> gpus)
    {
        var valid = gpus.Where(g => g.PowerW is double w && double.IsFinite(w) && w >= 0).Select(g => g.PowerW!.Value).ToList();
        var complete = gpus.Count > 0 && valid.Count == gpus.Count;
        return new PowerTotal(complete ? valid.Sum() : null, valid.Count, gpus.Count);
    }

    /// <summary>Board power × rate for one hour (an estimate of GPU board power only). Null unless complete.</summary>
    public decimal? CostPerHour(decimal rateKrwPerKwh)
        => TotalW is double w && rateKrwPerKwh >= 0 ? Math.Round((decimal)(w / 1000.0) * rateKrwPerKwh, 0, MidpointRounding.AwayFromZero) : null;
}

/// <summary>
/// The 12-byte HID report of the Antec FLUX PRO side-panel display (docs/06 §1.2): header 55 AA 01 01 06, CPU and GPU
/// temperature as tens/ones/tenths digits, and a checksum (sum of bytes 0..10, low byte). Stage 1 only shows the report
/// it would send; nothing is sent.
/// </summary>
public static class FluxPacket
{
    public const int Length = 12;
    private static readonly byte[] Header = [0x55, 0xAA, 0x01, 0x01, 0x06];

    public static byte[] Build(double? cpuC, double? gpuC)
    {
        var packet = new byte[Length];
        Header.CopyTo(packet, 0);
        Digits(cpuC, packet, 5);
        Digits(gpuC, packet, 8);
        var sum = 0;
        for (var i = 0; i < Length - 1; i++)
        {
            sum += packet[i];
        }

        packet[Length - 1] = (byte)(sum & 0xFF);
        return packet;
    }

    public static string Hex(byte[] packet) => string.Join(' ', packet.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>A missing value is sent as 00.0 (the display has no "unknown"); values are clamped to 0..99.9.</summary>
    private static void Digits(double? celsius, byte[] packet, int offset)
    {
        var tenths = celsius is double c && double.IsFinite(c) ? (int)Math.Round(Math.Clamp(c, 0, 99.9) * 10, MidpointRounding.AwayFromZero) : 0;
        packet[offset] = (byte)(tenths / 100);
        packet[offset + 1] = (byte)(tenths / 10 % 10);
        packet[offset + 2] = (byte)(tenths % 10);
    }
}

/// <summary>Window width → layout (docs/03 §4, tokens dpi.halfWidthBreakpointDip). Sizes are never fixed: they follow the window.</summary>
public enum LayoutMode
{
    /// <summary>≥ 1200 DIP (4K 175 % maximized = 2194 DIP): dual GPU side by side.</summary>
    Full,

    /// <summary>960–1199 DIP (4K 175 % half width = 1097 DIP): GPUs stacked, compact type, 4 system modules in one row.</summary>
    Compact,

    /// <summary>&lt; 960 DIP: everything stacked, system modules 2×2.</summary>
    Narrow,
}

public static class LayoutRules
{
    public const double CompactBelowDip = 1200;
    public const double NarrowBelowDip = 960;

    public static LayoutMode For(double widthDip)
        => widthDip >= CompactBelowDip ? LayoutMode.Full : widthDip >= NarrowBelowDip ? LayoutMode.Compact : LayoutMode.Narrow;

    public static int GpuColumns(LayoutMode mode) => mode == LayoutMode.Full ? 2 : 1;

    /// <summary>Four modules in one row, or an even 2×2 — never 3 + 1 (independent review).</summary>
    public static int SystemColumns(LayoutMode mode) => mode == LayoutMode.Narrow ? 2 : 4;

    /// <summary>
    /// Processes (WDDM) and WireView side by side down to the half-width window: at 1097 DIP the table keeps all five
    /// columns readable and the pin meters keep their full height; stacking them would halve the meters for nothing.
    /// Below 960 DIP they stack.
    /// </summary>
    public static bool WorkAreaSideBySide(LayoutMode mode) => mode != LayoutMode.Narrow;

    public static double WorkAreaFirstShare(LayoutMode mode) => mode == LayoutMode.Full ? 0.64 : 0.60;

    /// <summary>Two cards side by side (devices, settings, power panels) unless narrow.</summary>
    public static bool PanelsSideBySide(LayoutMode mode) => mode != LayoutMode.Narrow;
}

/// <summary>Number formatting of the instrument (tabular digits come from the font; missing values are "—").</summary>
public static class Fmt
{
    public const string Missing = "—";
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Int(double? value) => value is double v ? v.ToString("0", C) : Missing;
    public static string One(double? value) => value is double v ? v.ToString("0.0", C) : Missing;
    public static string Two(double? value) => value is double v ? v.ToString("0.00", C) : Missing;
    public static string Celsius(double? value) => value is double v ? v.ToString("0", C) + "°C" : Missing;
    public static string Watts(double? value) => value is double v ? v.ToString("0", C) + "W" : "N/A";
    public static string Mhz(double? value) => value is double v ? v.ToString("0", C) + " MHz" : Missing;
    public static string Percent2(double? value) => value is double v ? v.ToString("00", C) + "%" : Missing;
    public static string Gb(double value) => value.ToString("0.00", C) + " GB";
    public static string Krw(decimal value) => value.ToString("#,0", C);
}
