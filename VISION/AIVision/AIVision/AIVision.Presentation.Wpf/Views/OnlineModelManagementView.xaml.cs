using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class OnlineModelManagementView : Window
{
    public OnlineModelManagementView()
    {
        InitializeComponent();
    }

    public OnlineModelManagementView(OnlineModelManagementViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
