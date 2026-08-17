namespace AIVision.Application.Configuration;

public sealed class ProductionStatsUiConfig
{
    public List<SummaryFieldConfig> SummaryFields { get; set; } = new();

    public List<string> DefectBuckets { get; set; } = new();

    public Dictionary<string, string> DefectLabelMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SummaryFieldConfig
{
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Path to value (e.g. Order.Code, Total, Yield, Duration).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Optional format specifier (e.g. "dt:yyyy/MM/dd HH:mm", "p2", "n0").
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Optional width (GridLength string) for the label column, e.g. "200", "Auto".
    /// </summary>
    public string? LabelWidth { get; set; }

    /// <summary>
    /// Optional width (GridLength string) for the value column, e.g. "*" or "220".
    /// </summary>
    public string? ValueWidth { get; set; }
}
