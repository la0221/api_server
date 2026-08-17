using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class ModelManagementView : Window
{
    public ModelManagementView()
    {
        InitializeComponent();
    }

    public ModelManagementView(ModelManagementViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

