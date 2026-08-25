using System;
using System.Windows;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.ViewModels;
using Window = System.Windows.Window;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>
/// 「模號穴號實時檢測」視窗。
/// <para>VM 由 DI 注入；View 負責兩件 VM 不該碰的事：ROI 疊層（要 UI 座標）與關窗時把管線收乾淨。</para>
/// <para>ROI 的操作全部在 <c>RoiToolBar</c> 裡（自訂範圍／恢復預設／顯示框／顯示矩形參數），
/// 這裡只把它接上預覽 —— 一行。</para>
/// </summary>
public partial class RealtimeInspectionView : Window
{
    private readonly RealtimeInspectionViewModel _viewModel;

    public RealtimeInspectionView(RealtimeInspectionViewModel viewModel, RoiSettings roi)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // ROI：接上就有全部功能。框選/重設後管線會自己換參數（RealtimeInspectionPipeline 訂了 RoiSettings.Changed），
        // 所以畫面畫的框永遠等於實際判定的區域。
        RoiTools.Attach(Preview, roi);
        RoiTools.Notice += (_, msg) => _viewModel.PushMessage(msg);

        // 影像更新走 VM 事件，不用 Binding：疊層要拿 BitmapSource 算座標
        _viewModel.PreviewUpdated += OnPreviewUpdated;
    }

    private void OnPreviewUpdated(object? sender, PreviewFrameEventArgs e) =>
        Preview.SetImage(e.Image, e.Hint);

    protected override async void OnClosed(EventArgs e)
    {
        // 關窗一定要停：不停的話相機事件與檢測迴圈會繼續跑，
        // 下次再開就變成兩條迴圈搶同一批幀。
        _viewModel.PreviewUpdated -= OnPreviewUpdated;
        await _viewModel.StopCommand.ExecuteAsync(null);
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
