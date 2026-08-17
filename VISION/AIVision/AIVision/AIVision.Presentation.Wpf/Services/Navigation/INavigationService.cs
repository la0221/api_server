using System.Windows;

namespace AIVision.Presentation.Wpf.Services.Navigation;

public interface INavigationService
{
    void ShowWindow<TWindow>() where TWindow : Window;

    bool? ShowDialog<TWindow>() where TWindow : Window;

    /// <summary>
    /// 顯示 Dialog 並在顯示前進行初始化
    /// </summary>
    bool? ShowDialog<TWindow>(Action<TWindow> initializer) where TWindow : Window;

    /// <summary>
    /// 創建視窗實例（不顯示）
    /// </summary>
    TWindow? CreateWindow<TWindow>() where TWindow : Window;
}
