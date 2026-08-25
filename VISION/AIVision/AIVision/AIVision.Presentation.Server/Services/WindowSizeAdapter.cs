using System;
using System.Windows;

namespace AIVision.Presentation.Server.Services;

/// <summary>
/// 視窗尺寸自適應。
///
/// 起因（2026-08-19）：驗證機是 1024x768 的工控機，多個視窗把 MinWidth 寫死 1100~1280 ——
/// 不只超出畫面，更因為卡在 MinWidth 而「連拉小都做不到」，右側按鈕被推到螢幕外按不到。
///
/// 改版（2026-08-24，UI 實測發現⑸ 後使用者拍板）：
/// **不要再用寫死的像素尺寸，一律用「螢幕工作區的百分比」，預設 80%。**
/// 舊版只在「太大時才夾小」（95% 上限），所以父端 1180x800 在大螢幕上仍是死的 1180x800，
/// 到 1024x768 又剛好裝不下（寬多 156、高多 72），CenterScreen 還會把標題列推到畫面上緣外。
///
/// 現在的規則：
/// <list type="bullet">
/// <item><b>可調整大小的內容視窗</b> → 尺寸 = <c>工作區 × 比例（預設 0.80）</c>，並置中。
///   XAML 上的 Width/Height 只當設計目標，實際尺寸永遠跟著螢幕走。</item>
/// <item><b>對話框</b>（<c>ResizeMode=NoResize</c> 或有設 <c>SizeToContent</c>）→ <b>維持設計尺寸</b>，
///   只在裝不下時才夾小。登入框、工單輸入這種東西放大到 80% 是災難。</item>
/// <item>兩者都會把 MinWidth/MinHeight 鬆綁並拉回工作區內。</item>
/// </list>
///
/// 比例可由 appsettings 的 <c>Ui:WindowRatio</c> 調整（0.3~1.0），現場覺得太小/太大不必改程式。
///
/// ⚠ 站端 <c>AIVision.Presentation.Wpf</c> 與父端 <c>AIVision.Presentation.Server</c> 各有一份**完全相同**的複本：
/// 兩支程式刻意零依賴（可各自安裝），沒辦法共用。**改一份就要改另一份。**
/// </summary>
public static class WindowSizeAdapter
{
    /// <summary>預設比例：視窗佔螢幕工作區的 80%。</summary>
    public const double DefaultRatio = 0.80;

    private static double _ratio = DefaultRatio;

    /// <summary>目前生效的比例。</summary>
    public static double Ratio => _ratio;

    /// <summary>
    /// 在 App.OnStartup 呼叫一次；之後每個 Window 載入時自動套用。
    /// <paramref name="ratio"/> 為 null 或超出 0.3~1.0 時用 <see cref="DefaultRatio"/>。
    /// </summary>
    public static void RegisterGlobal(double? ratio = null)
    {
        if (ratio is > 0.3 and <= 1.0) _ratio = ratio.Value;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Apply(s as Window)));
    }

    /// <summary>套用到單一視窗。已最大化者不動（交給系統）。</summary>
    public static void Apply(Window? w)
    {
        if (w is null || w.WindowState == WindowState.Maximized) return;

        // WorkArea 已扣掉工作列，比 PrimaryScreenWidth 更貼近可用範圍
        var wa = SystemParameters.WorkArea;
        if (wa.Width <= 0 || wa.Height <= 0) return;

        var targetW = wa.Width * _ratio;
        var targetH = wa.Height * _ratio;

        // 對話框不放大：ResizeMode 鎖住、或用 SizeToContent 自己算高度的，都屬這類。
        var isDialog = w.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize
                       || w.SizeToContent != SizeToContent.Manual;

        // ① Min 先鬆綁：不解開的話，下面設 Width 會被 MinWidth 拉回去，視窗依舊縮不小
        if (w.MinWidth > targetW) w.MinWidth = targetW;
        if (w.MinHeight > targetH) w.MinHeight = targetH;

        if (isDialog)
        {
            // 只夾小，不放大。XAML 沒寫 Width 時 w.Width 是 NaN，要改看 ActualWidth
            var curW = double.IsNaN(w.Width) ? w.ActualWidth : w.Width;
            var curH = double.IsNaN(w.Height) ? w.ActualHeight : w.Height;
            if (curW > targetW) w.Width = targetW;
            if (curH > targetH && w.SizeToContent == SizeToContent.Manual) w.Height = targetH;
        }
        else
        {
            // 內容視窗一律吃比例——這才是「不用寫死像素」的意思
            w.Width = targetW;
            w.Height = targetH;
        }

        // ② 置中並拉回螢幕內：CenterScreen/CenterOwner 是依「改尺寸前」算的，改完可能已經偏出去
        var finalW = double.IsNaN(w.Width) ? w.ActualWidth : w.Width;
        var finalH = double.IsNaN(w.Height) ? w.ActualHeight : w.Height;

        if (!isDialog)
        {
            w.Left = wa.Left + (wa.Width - finalW) / 2;
            w.Top = wa.Top + (wa.Height - finalH) / 2;
        }

        if (double.IsNaN(w.Left) || double.IsNaN(w.Top)) return;
        if (w.Left < wa.Left) w.Left = wa.Left;
        if (w.Top < wa.Top) w.Top = wa.Top;
        if (w.Left + finalW > wa.Right) w.Left = Math.Max(wa.Left, wa.Right - finalW);
        if (w.Top + finalH > wa.Bottom) w.Top = Math.Max(wa.Top, wa.Bottom - finalH);
    }
}
