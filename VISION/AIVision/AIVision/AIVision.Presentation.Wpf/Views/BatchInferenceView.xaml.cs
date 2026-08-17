using System;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class BatchInferenceView : Window
{
    private readonly BatchInferenceViewModel _viewModel;

    public BatchInferenceView(BatchInferenceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _viewModel.DisposeAsync();
    }
}

