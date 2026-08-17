using System.Windows;
using System.Windows.Input;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class WorkOrderManagementView : Window
{
    public WorkOrderManagementView(WorkOrderManagementViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    // 雙擊列 = 設為目前工單（比點小按鈕直覺）。
    private void WorkOrderGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is WorkOrderManagementViewModel vm &&
            WorkOrderGrid.SelectedItem is WorkOrderItemViewModel &&
            vm.SwitchToWorkOrderCommand.CanExecute(null))
        {
            vm.SwitchToWorkOrderCommand.Execute(null);
        }
    }
}
