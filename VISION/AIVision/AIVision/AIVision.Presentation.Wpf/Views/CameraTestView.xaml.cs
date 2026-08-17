using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class CameraTestView : Window
{
    public CameraTestView(CameraTestViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
