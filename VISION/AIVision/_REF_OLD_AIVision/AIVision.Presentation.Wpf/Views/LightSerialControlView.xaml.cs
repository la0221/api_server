using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class LightSerialControlView : Window
{
    public LightSerialControlView(LightSerialControlViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
