using System.Windows;
using AIVision.Presentation.Server.ViewModels;

namespace AIVision.Presentation.Server.Views;

/// <summary>自我強化訓練視窗（父端）。</summary>
public partial class TrainingWindow : Window
{
    private readonly TrainingViewModel _viewModel;

    public TrainingWindow(TrainingViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) => await _viewModel.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) => _viewModel.Dispose();   // 停掉輪詢，免得關窗後還在打 API
    }
}
