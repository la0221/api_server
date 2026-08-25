using System;
using System.Diagnostics;
using System.Windows;
using System.ComponentModel;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class ShellView : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly RoiSettings _roi;
    private bool _isCleanupDone = false;

    public ShellView(ShellViewModel viewModel, RoiSettings roi)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _roi = roi;
        DataContext = viewModel;

        // ROI 框永遠疊在主頁預覽上——它就是「送去辨識的範圍」。
        // 現場最常見的問題是 ROI 沒對準（工件沒完整落在框內 → 擷取失誤或讀值錯），
        // 畫面上看得到框才查得出來，而且要能當場改。
        // 疊層與四個 ROI 功能都在共用控制項裡，這裡只接線 —— 主頁不再自己寫一份座標換算。
        _roi.Load();
        RoiTools.Attach(Preview, _roi);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePreview();

        // 一進面板就把相機即時預覽接起來——不必先按「開始」也不必有工單。
        // 失敗不跳視窗，原因會顯示在預覽區中央（見 ShellViewModel.StartLivePreviewAsync）。
        Loaded += async (_, _) => await _viewModel.StartLivePreviewCommand.ExecuteAsync(null);

        // ✅ 使用 Closing 事件來執行清理
        Closing += OnWindowClosing;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.LiveBitmap)
                           or nameof(ShellViewModel.LivePreviewHint))
        {
            UpdatePreview();
        }
    }

    /// <summary>把 VM 的影像餵給共用預覽控制項（框、座標、框選由它自己處理）。</summary>
    private void UpdatePreview() =>
        Preview.SetImage(_viewModel.LiveBitmap, _viewModel.LivePreviewHint);

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 如果已經清理過，直接允許關閉
        if (_isCleanupDone)
        {
            Debug.WriteLine("[ShellView] Closing - 清理已完成，允許關閉");
            return;
        }

        // ✅ 取消第一次關閉請求，執行清理
        e.Cancel = true;
        Debug.WriteLine("[ShellView] Closing - 開始清理（取消關閉請求）...");
        var sw = Stopwatch.StartNew();

        try
        {
            // 使用超時機制確保不會無限等待
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var cleanupTask = _viewModel.CleanupAsync();

            // 等待清理完成或超時
            var completedTask = await Task.WhenAny(cleanupTask, Task.Delay(5000, cts.Token));

            if (completedTask != cleanupTask)
            {
                Debug.WriteLine("[ShellView] Closing - 清理超時 (5秒)，強制繼續");
            }
            else
            {
                // 確保例外被處理
                await cleanupTask;
                Debug.WriteLine($"[ShellView] Closing - 清理完成，耗時 {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShellView] Closing - 清理發生錯誤: {ex.Message}");
        }
        finally
        {
            _viewModel.Dispose();
            Debug.WriteLine($"[ShellView] Closing - Dispose 完成，總耗時 {sw.ElapsedMilliseconds}ms");

            _isCleanupDone = true;

            // ✅ 延遲讓 LOG 寫入完成，然後強制結束
            await Task.Delay(100);
            Debug.WriteLine("[ShellView] Closing - 呼叫 Environment.Exit(0)");

            // ✅ 直接使用 Environment.Exit(0) 強制終止
            Environment.Exit(0);
        }
    }

}
