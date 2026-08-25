using System.Windows;
using AIVision.Presentation.Server.ViewModels;

namespace AIVision.Presentation.Server.Views;

/// <summary>單筆收件的詳細視窗（站點細節頁再點進去）。</summary>
public partial class RecordDetailWindow : Window
{
    private readonly RecordDetailViewModel _viewModel;

    public RecordDetailWindow(RecordDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }
}
