using System.Windows;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>
/// Splash Screen 啟動畫面
/// </summary>
public partial class SplashWindow : Window
{
    private readonly ILogger<SplashWindow>? _logger;

    public SplashWindow(ILogger<SplashWindow>? logger = null)
    {
        InitializeComponent();
        _logger = logger;
        _logger?.LogInformation("[Splash] 顯示 Splash Screen");
    }

    /// <summary>
    /// 更新載入狀態文字
    /// </summary>
    /// <param name="status">狀態文字</param>
    public void UpdateStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingStatus.Text = status;
            _logger?.LogInformation("[Splash] 狀態更新: {Status}", status);
        });
    }

    /// <summary>
    /// 設定進度百分比（0-100）
    /// </summary>
    /// <param name="percent">百分比</param>
    public void SetProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingProgress.IsIndeterminate = false;
            LoadingProgress.Value = percent;
        });
    }

    /// <summary>
    /// 關閉 Splash Screen
    /// </summary>
    public new void Close()
    {
        _logger?.LogInformation("[Splash] 關閉 Splash Screen");
        base.Close();
    }
}
