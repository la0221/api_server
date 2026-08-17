namespace AIVision.Presentation.Wpf.ViewModels;

public sealed class DefectRowViewModel
{
    public DefectRowViewModel(string label, int count, double ratio)
    {
        Label = label;
        Count = count;
        Ratio = ratio;
    }

    public string Label { get; }

    public int Count { get; }

    public double Ratio { get; }
}
