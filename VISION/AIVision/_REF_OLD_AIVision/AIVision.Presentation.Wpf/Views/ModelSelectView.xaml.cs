using System;
using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class ModelSelectView : Window
{
    public ModelSelectView(ModelSelectViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        
        viewModel.ModelSelected += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

