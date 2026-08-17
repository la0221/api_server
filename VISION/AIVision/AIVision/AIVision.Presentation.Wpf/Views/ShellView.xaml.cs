using System.Diagnostics;
using System.Windows;
using System.ComponentModel;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class ShellView : Window
{
    private readonly ShellViewModel _viewModel;
    private bool _isCleanupDone = false;

    public ShellView(ShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // ✅ 使用 Closing 事件來執行清理
        Closing += OnWindowClosing;
    }

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
