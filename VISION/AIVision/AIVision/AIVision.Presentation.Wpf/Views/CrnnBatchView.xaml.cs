using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>CRNN 測試（中央推論）視窗——引擎並行期的 CRNN 專屬批量測試。</summary>
public partial class CrnnBatchView : Window
{
    public CrnnBatchView(CrnnBatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
