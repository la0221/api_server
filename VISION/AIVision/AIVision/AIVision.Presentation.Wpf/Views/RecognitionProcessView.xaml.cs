using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class RecognitionProcessView : Window
{
    public RecognitionProcessView(RecognitionProcessViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
