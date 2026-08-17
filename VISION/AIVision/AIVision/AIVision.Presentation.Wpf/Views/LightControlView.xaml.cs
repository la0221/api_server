using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class LightControlView : Window
{
    public LightControlView(LightControlViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
