using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>
/// 「站端送檢（前處理下放）」視窗——原圖留本站、只把前處理小圖送中央推論。
/// 對應 2026-08-14 跨機實測通過的資料流（讀值不折損、傳輸量 −68.6%）。
/// </summary>
public partial class RouteAEdgeView : Window
{
    public RouteAEdgeView(RouteAEdgeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
