namespace LocalAiPerf.Presentation.Charts;

/// <summary>One line. X is an OLE Automation date (local time) for time charts, or minutes for elapsed-time charts.</summary>
public sealed record ChartSeries(string Label, double[] X, double[] Y, string ColorHex, bool IsDerived = false);

/// <summary>A highlighted X range (for example a degradation candidate).</summary>
public sealed record ChartSpan(double X1, double X2, string ColorHex, string Label);

/// <summary>A horizontal reference in the same unit as the chart (for example the GPU power limit).</summary>
public sealed record ChartReferenceLine(double Y, string Label, string ColorHex);

/// <summary>
/// Everything a chart view needs. One unit per chart (never two y-axes). The view draws a legend only for two or more series.
/// </summary>
public sealed record ChartModel
{
    public required string Title { get; init; }
    public required string Unit { get; init; }
    public IReadOnlyList<ChartSeries> Series { get; init; } = [];
    public IReadOnlyList<ChartSpan> Spans { get; init; } = [];
    public IReadOnlyList<ChartReferenceLine> ReferenceLines { get; init; } = [];
    public bool XIsTime { get; init; } = true;
    public string? XLabel { get; init; }

    /// <summary>Points further apart than this (in X units) are not connected: a gap in data stays visible as a gap.</summary>
    public double GapThreshold { get; init; } = double.PositiveInfinity;

    public double? YMin { get; init; }
    public double? YMax { get; init; }

    /// <summary>Format used by the hover readout.</summary>
    public string ValueFormat { get; init; } = "0.0";

    public string EmptyText { get; init; } = "표시할 데이터가 없습니다 (N/A)";

    public bool IsEmpty => Series.All(s => s.X.Length == 0);
}

/// <summary>
/// Validated dark-mode palette (categorical order is fixed; colors follow the entity, never its rank).
/// Status colors are reserved for states and are always shown with a text label.
/// </summary>
public static class ChartPalette
{
    public static readonly string[] Categorical =
        ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

    public const string Surface = "#1a1a19";
    public const string PrimaryInk = "#f0efec";
    public const string SecondaryInk = "#c3c2b7";
    public const string MutedInk = "#898781";
    public const string Grid = "#2c2c2a";
    public const string Axis = "#383835";

    public const string Good = "#0ca30c";
    public const string Warning = "#fab219";
    public const string Serious = "#ec835a";
    public const string Critical = "#d03b3b";

    public static string Slot(int index) => Categorical[((index % Categorical.Length) + Categorical.Length) % Categorical.Length];
}
