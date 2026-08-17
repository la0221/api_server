using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>模型發布視窗（工程師以上；選用途→選檔→版本號→上傳到伺服器登錄夾）。</summary>
public partial class ModelPublishView : Window
{
    public ModelPublishView(ModelPublishViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
