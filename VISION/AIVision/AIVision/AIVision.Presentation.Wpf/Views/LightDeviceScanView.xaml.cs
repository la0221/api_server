using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class LightDeviceScanView : Window
{
    public LightDeviceScanView(LightDeviceScanViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
