using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class OfflineModelManagementView : Window
{
    public OfflineModelManagementView()
    {
        InitializeComponent();
    }

    public OfflineModelManagementView(OfflineModelManagementViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
