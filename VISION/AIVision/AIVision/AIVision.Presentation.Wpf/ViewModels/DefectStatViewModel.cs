using CommunityToolkit.Mvvm.ComponentModel;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class DefectStatViewModel : ObservableObject
{
    public DefectStatViewModel(string label)
    {
        _label = label;
    }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private double _ratio;
}
