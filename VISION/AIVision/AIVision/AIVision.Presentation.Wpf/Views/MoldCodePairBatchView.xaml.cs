using System.Windows;
using System.Windows.Input;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class MoldCodePairBatchView : Window
{
    public MoldCodePairBatchView(MoldCodePairBatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MoldCodePairBatchViewModel vm &&
            ResultsGrid.SelectedItem is MoldCodePairBatchRow row &&
            vm.ShowProcessCommand.CanExecute(row))
        {
            vm.ShowProcessCommand.Execute(row);
        }
    }
}
