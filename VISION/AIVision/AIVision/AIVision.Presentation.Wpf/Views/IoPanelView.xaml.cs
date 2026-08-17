using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class IoPanelView : Window
{
    public IoPanelView(IoPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
