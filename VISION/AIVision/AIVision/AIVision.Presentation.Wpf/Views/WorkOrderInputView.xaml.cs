using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class WorkOrderInputView : Window
{
    public WorkOrderInputView(WorkOrderInputViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetWindow(this);
    }
}
