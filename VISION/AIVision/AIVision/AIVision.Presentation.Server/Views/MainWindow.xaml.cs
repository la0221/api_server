using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Infrastructure.MoldCode;
using AIVision.Presentation.Server.ViewModels;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Server.Views;

/// <summary>
/// 中央推論機（父端）主視窗——服務狀態、站點卡、最近辨識紀錄。
/// <para>「點進去看細節」的視窗由這裡開：VM 只丟出委派，不直接碰 Window（保持可測）。</para>
/// </summary>
public partial class MainWindow : Window
{
    private readonly ServerMonitorViewModel _viewModel;
    private readonly InferenceMonitorClient _monitor;
    private readonly ILoggerFactory? _loggerFactory;

    public MainWindow(
        ServerMonitorViewModel viewModel,
        InferenceMonitorClient monitor,
        ILoggerFactory? loggerFactory = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _monitor = monitor;
        _loggerFactory = loggerFactory;
        DataContext = viewModel;

        _viewModel.OpenStationDetail = OpenStationDetailAsync;
        _viewModel.OpenRecordDetail = OpenRecordDetailAsync;

        // 開窗即健檢一次；預設每 5 秒自動更新，關窗停掉計時器。
        Loaded += async (_, _) => await _viewModel.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>開自我強化訓練視窗（訓練跑在這台中央機上）。</summary>
    private void OnOpenTrainingClick(object sender, RoutedEventArgs e)
    {
        var vm = new TrainingViewModel(_monitor, _loggerFactory?.CreateLogger<TrainingViewModel>());
        new TrainingWindow(vm) { Owner = this }.Show();
    }

    private Task OpenStationDetailAsync(StationRow row)
    {
        var vm = new StationDetailViewModel(
            row.GroupKey, row.GroupName, _monitor,
            OpenRecordDetailAsync,
            _loggerFactory?.CreateLogger<StationDetailViewModel>());

        var win = new StationDetailWindow(vm) { Owner = this };
        win.Show();
        return Task.CompletedTask;
    }

    private Task OpenRecordDetailAsync(RecentInferenceRow row)
    {
        var vm = new RecordDetailViewModel(
            row.Seq, _monitor,
            _loggerFactory?.CreateLogger<RecordDetailViewModel>());

        // Owner 設成「目前作用中的視窗」：從站點細節頁點進來時，才會疊在那個視窗上而不是跳回主視窗。
        // ⚠ 要寫完整名稱：本組件同時參考 AIVision.Application 命名空間，
        // 直接寫 Application 會被解析成那個命名空間而編譯失敗。
        var owner = System.Windows.Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? this;
        var win = new RecordDetailWindow(vm) { Owner = owner };
        win.Show();
        return Task.CompletedTask;
    }
}
