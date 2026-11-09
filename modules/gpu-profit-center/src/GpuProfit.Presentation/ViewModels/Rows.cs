namespace GpuProfit.Presentation.ViewModels;

public sealed record NameValueRow(string Name, string Value, string Note = "");

/// <summary>A labelled amount with its provenance (실측 / 사용자 입력 / 외부 보고 / 추정·가정 / 계산) and how it was obtained.</summary>
public sealed record BreakdownRow(string Item, string Value, string Origin, string Basis);

public sealed record GpuCardRow(int Index, string Name, string Power, string Utilization, string Temperature, string Vram, string Clocks, string Status, bool Ok);

public sealed record SessionRow(long Id, string Title, string Workload, string Type, string State, string Started, string Hours, string Energy,
    string RealizedNet, string Flags, bool IsOpen, bool IsMock);

public sealed record ActiveSessionRow(long Id, string Title, string Workload, string State, string Elapsed, string Energy, string Cost, string Coverage,
    string Activity);

public sealed record PeriodSummaryRow(string Period, string Sessions, string Energy, string EstimatedShare, string Cost, string Revenue, string Net, string Note);

public sealed record SegmentRow(string Start, string End, string Duration, string EndReason);

public sealed record GapRow(string From, string To, string Duration, string Kind, string Detail);

public sealed record DeviceEnergyRow(string Gpu, string Measured, string Observed, string Coverage, string AveragePower, string MinMax, string MedianP95, string Samples,
    string Counter, string LongestGap);

public sealed record RevenueRow(long Id, string Kind, string Amount, string Krw, string Occurred, string Provider, string Origin, string Note);

public sealed record CostRow(long Id, string Scope, string Category, string Basis, string Amount, string Active, string Note);

public sealed record ResultHistoryRow(string Computed, string Reason, string Energy, string ElectricityCost, string RealizedNet, string Hash, string Conditions);

public sealed record CapabilityRow(string Metric, string Source, string Status, string Detail);
