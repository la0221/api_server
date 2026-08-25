using System.Windows;
using AIVision.Presentation.Server.ViewModels;

namespace AIVision.Presentation.Server.Views;

/// <summary>
/// 站點細節視窗（模號穴號／公母模／瑕疵檢查點進來看到的）。
/// 內容一律條列式；再點單筆可開 <see cref="RecordDetailWindow"/>。
/// </summary>
public partial class StationDetailWindow : Window
{
    private readonly StationDetailViewModel _viewModel;

    public StationDetailWindow(StationDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = $"站點細節 — {viewModel.GroupName}";

        Loaded += async (_, _) => await _viewModel.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) => _viewModel.Dispose();   // 停掉自動更新計時器，免得關窗後還在打 API
    }
}
