using System;
using System.Windows;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class ModelEditView : Window
{
    public ModelEditView(ModelEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += (_, result) =>
        {
            DialogResult = result;
            Close();
        };
    }

    private void SingleModelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModelEditViewModel vm)
        {
            vm.SelectedInferenceType = InferenceType.SingleModel;
        }
    }

    private void WorkflowRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModelEditViewModel vm)
        {
            vm.SelectedInferenceType = InferenceType.Workflow;
        }
    }
}

