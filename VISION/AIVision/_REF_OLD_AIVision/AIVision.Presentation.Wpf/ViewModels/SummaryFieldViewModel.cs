namespace AIVision.Presentation.Wpf.ViewModels;

public sealed class SummaryFieldViewModel
{
    public SummaryFieldViewModel(string label, string value, string? labelWidth = null, string? valueWidth = null)
    {
        Label = label;
        FormattedValue = value;
        LabelWidth = labelWidth;
        ValueWidth = valueWidth;
    }

    public string Label { get; }

    public string FormattedValue { get; }

    public string? LabelWidth { get; }

    public string? ValueWidth { get; }
}
