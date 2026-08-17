using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>API 伺服器設定視窗（中央推論 server 選擇/測試連線）。</summary>
public partial class ServerSettingsView : Window
{
    public ServerSettingsView(ServerSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
