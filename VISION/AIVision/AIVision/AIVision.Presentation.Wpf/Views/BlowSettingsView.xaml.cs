using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>吹氣觸發設定視窗（移植自 模號檢驗/相機版 的 BlowSettingsWindow）。</summary>
public partial class BlowSettingsView : Window
{
    public BlowSettingsView(BlowSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
