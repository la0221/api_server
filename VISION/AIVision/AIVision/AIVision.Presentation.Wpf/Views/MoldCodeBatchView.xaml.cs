using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class MoldCodeBatchView : Window
{
    public MoldCodeBatchView(MoldCodeBatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
