using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AIVision.Presentation.Wpf.Services;
// 專案同時引用 System.Windows.Forms（AForge 相機），Point/UserControl/滑鼠事件全部撞名 → 用別名釘死
using UserControl = System.Windows.Controls.UserControl;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Window = System.Windows.Window;


namespace AIVision.Presentation.Wpf.Views.Controls;

/// <summary>
/// 影像預覽 + ROI 疊層（顯示、拖曳重新框選、矩形參數）。移植自已驗證的 <c>模號檢驗/相機版</c>。
///
/// <para><b>ROI 框預設永遠畫出來</b>——它就是「送去辨識的範圍」。現場最常見的問題是
/// ROI 沒對準（工件沒完整落在框內 → 擷取失誤或讀值錯），畫面上看得到框才查得出來。</para>
///
/// <para><b>操作按鈕不在這裡</b>：四個 ROI 功能全部集中在 <c>RoiToolBar</c>（同一組、同一個地方）。
/// 這個控制項只負責「畫」與「量」。要放到別的畫面就是
/// <c>RoiPreview</c> ＋ <c>RoiToolBar</c> 兩行，不會再長出第三套 ROI 程式碼。</para>
/// </summary>
public partial class RoiPreview : UserControl
{
    private RoiSettings? _roi;
    private bool _pickMode;
    private bool _dragging;
    private Point _dragStart;
    private Window? _escHost;          // 框選期間借用的視窗（掛 Esc 取消）
    private bool _showRoi = true;
    private bool _showCoords = true;
    private string _description = "";

    public RoiPreview()
    {
        InitializeComponent();
        ViewHost.SizeChanged += (_, _) => UpdateOverlay();
        Loaded += (_, _) => UpdateOverlay();
        Unloaded += (_, _) => DetachEscHost();
    }

    /// <summary>ROI 被改動（框選完成或重設），**已存檔**。</summary>
    public event EventHandler? RoiChanged;

    /// <summary>要給使用者看的訊息（框太小、還沒有影像…）。</summary>
    public event EventHandler<string>? Notice;

    /// <summary>目前生效 ROI 的說明文字有變（工具列顯示用）。</summary>
    public event EventHandler? DescriptionChanged;

    /// <summary>是否顯示 ROI 框。設了立刻重畫。</summary>
    public bool ShowRoi
    {
        get => _showRoi;
        set { if (_showRoi == value) return; _showRoi = value; UpdateOverlay(); }
    }

    /// <summary>是否顯示矩形參數（座標面板）。純顯示，不影響裁切/辨識等作動。</summary>
    public bool ShowCoords
    {
        get => _showCoords;
        set { if (_showCoords == value) return; _showCoords = value; UpdateOverlay(); }
    }

    /// <summary>無影像時那行提示的字級（主頁要大、面板要小）。</summary>
    public double HintFontSize
    {
        get => TxtHint.FontSize;
        set => TxtHint.FontSize = value;
    }

    /// <summary>目前正在框選中。</summary>
    public bool IsPicking => _pickMode;

    /// <summary>目前顯示的影像。</summary>
    public BitmapSource? CurrentImage => ImgLive.Source as BitmapSource;

    /// <summary>目前生效的 ROI（像素＋比例）一行字。</summary>
    public string Description => _description;

    /// <summary>綁定 ROI 設定來源；設定變更時自動重畫。</summary>
    public void Attach(RoiSettings roi)
    {
        if (_roi is not null) _roi.Changed -= OnRoiSettingsChanged;
        _roi = roi;
        _roi.Changed += OnRoiSettingsChanged;
        UpdateOverlay();
    }

    /// <summary>更新影像。傳 null 代表沒有畫面（會顯示 <paramref name="hint"/>）。</summary>
    public void SetImage(BitmapSource? image, string? hint = null)
    {
        ImgLive.Source = image;
        TxtHint.Text = image is null ? (hint ?? "") : "";
        TxtHint.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateOverlay();
    }

    /// <summary>進入框選模式（在畫面上拖曳）。沒有影像時不給進——沒有基準座標可換算。</summary>
    public bool BeginPick()
    {
        if (ImgLive.Source is not BitmapSource)
        {
            Notice?.Invoke(this, "還沒有影像，無法框選 ROI —— 請先啟動相機/預覽。");
            return false;
        }
        _pickMode = true;
        PickHint.Visibility = Visibility.Visible;
        HostBorder.Cursor = Cursors.Cross;

        // Esc 取消（同相機版）。掛在所屬視窗上，這樣每個用到的畫面都不必自己再寫一次。
        DetachEscHost();
        _escHost = Window.GetWindow(this);
        if (_escHost is not null) _escHost.PreviewKeyDown += OnHostKeyDown;

        Notice?.Invoke(this, "框選 ROI：在畫面上按住左鍵拖出範圍，放開完成（Esc 取消）。");
        Focus();
        return true;
    }

    /// <summary>離開框選模式（不改 ROI）。</summary>
    public void CancelPick()
    {
        _pickMode = false;
        _dragging = false;
        RoiSelRect.Visibility = Visibility.Collapsed;
        PickHint.Visibility = Visibility.Collapsed;
        HostBorder.Cursor = Cursors.Arrow;
        HostBorder.ReleaseMouseCapture();
        DetachEscHost();
        UpdateOverlay();
    }

    /// <summary>重設回預設 ROI（會存檔、立即生效）。</summary>
    public void ResetRoi()
    {
        if (_roi is null) return;
        _roi.Reset();
        // 這裡**不要**把 _description 接上去：重畫是排進 Dispatcher 佇列的，
        // 此刻那個字串還是舊值，貼出來會顯示錯的數字。新值由「目前 ROI」那一行負責。
        Notice?.Invoke(this, "ROI 已重設為預設值並存檔（立即生效）。");
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DetachEscHost()
    {
        if (_escHost is null) return;
        _escHost.PreviewKeyDown -= OnHostKeyDown;
        _escHost = null;
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_pickMode) return;
        CancelPick();
        Notice?.Invoke(this, "框選 ROI 已取消，ROI 維持原值。");
        e.Handled = true;
    }

    private void OnRoiSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(UpdateOverlay));

    // ── 座標換算 ────────────────────────────────────────────────────────

    /// <summary>
    /// 顯示座標（ViewHost 相對）→ 影像像素座標，夾在影像範圍內。
    /// <para>以 <c>ViewHost</c>（Grid，尺寸一定有效）為基準，不用 Canvas.ActualWidth——
    /// 後者在某些機器/時序會回 0，造成除以零、ROI 消失或框選失效（相機版踩過）。</para>
    /// </summary>
    private Point DisplayToPixel(Point p)
    {
        if (ImgLive.Source is not BitmapSource bmp) return new Point(0, 0);
        double iw = bmp.PixelWidth, ih = bmp.PixelHeight;
        double aw = ViewHost.ActualWidth, ah = ViewHost.ActualHeight;
        if (iw <= 0 || ih <= 0 || aw <= 0 || ah <= 0) return new Point(0, 0);

        double scale = Math.Min(aw / iw, ah / ih);
        double offsetX = (aw - iw * scale) / 2;
        double offsetY = (ah - ih * scale) / 2;
        return new Point(
            Math.Clamp((p.X - offsetX) / scale, 0, iw),
            Math.Clamp((p.Y - offsetY) / scale, 0, ih));
    }

    // ── 疊層 ────────────────────────────────────────────────────────────

    private void UpdateOverlay()
    {
        if (_roi is null || ImgLive.Source is not BitmapSource bmp)
        {
            HideRoi();
            return;
        }

        double iw = bmp.PixelWidth, ih = bmp.PixelHeight;
        double aw = ViewHost.ActualWidth, ah = ViewHost.ActualHeight;
        if (iw <= 0 || ih <= 0 || aw <= 0 || ah <= 0)
        {
            HideRoi();
            return;
        }

        var (rx, ry, rw, rh) = _roi.ToPixels((int)iw, (int)ih);

        // 說明文字與「框顯不顯示」無關 —— 把框關掉也要查得到目前生效的是哪一組值
        SetDescription(_roi.Describe((int)iw, (int)ih));

        if (!_showRoi)
        {
            HideRoi(keepDescription: true);
            return;
        }

        double scale = Math.Min(aw / iw, ah / ih);
        double offsetX = (aw - iw * scale) / 2;
        double offsetY = (ah - ih * scale) / 2;

        double x = offsetX + rx * scale;
        double y = offsetY + ry * scale;
        RoiRect.Width = rw * scale;
        RoiRect.Height = rh * scale;
        Canvas.SetLeft(RoiRect, x);
        Canvas.SetTop(RoiRect, y);
        Canvas.SetLeft(RoiLabelBox, x + 4);
        Canvas.SetTop(RoiLabelBox, y + 3);
        RoiRect.Visibility = Visibility.Visible;
        RoiLabelBox.Visibility = Visibility.Visible;

        SetCoordText(rx, ry, rw, rh, (int)iw, (int)ih);
    }

    private void HideRoi(bool keepDescription = false)
    {
        RoiRect.Visibility = Visibility.Collapsed;
        RoiLabelBox.Visibility = Visibility.Collapsed;
        CoordPanel.Visibility = Visibility.Collapsed;
        if (!keepDescription) SetDescription("");
    }

    private void SetDescription(string text)
    {
        if (_description == text) return;
        _description = text;
        DescriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>矩形參數：四角 + 尺寸 + 影像 + 比例（同相機版）。純顯示，可獨立開關。</summary>
    private void SetCoordText(int x, int y, int w, int h, int frameW, int frameH)
    {
        if (!_showCoords)
        {
            CoordPanel.Visibility = Visibility.Collapsed;
            return;
        }
        int x1 = x + w, y1 = y + h;
        string pct = frameW > 0 && frameH > 0
            ? $"\n比例 ({(double)x / frameW:0.000}, {(double)y / frameH:0.000}, "
              + $"{(double)w / frameW:0.000}, {(double)h / frameH:0.000})"
            : "";
        TxtCoord.Text =
            $"左上 ({x,4},{y,4})   右上 ({x1,4},{y,4})\n" +
            $"左下 ({x,4},{y1,4})   右下 ({x1,4},{y1,4})\n" +
            $"尺寸 {w}×{h}   影像 {frameW}×{frameH}{pct}";
        CoordPanel.Visibility = Visibility.Visible;
    }

    // ── 拖曳框選 ────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_pickMode || ImgLive.Source is null) return;
        _dragging = true;
        _dragStart = e.GetPosition(ViewHost);
        RoiSelRect.Visibility = Visibility.Visible;
        HostBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(ViewHost);
        Canvas.SetLeft(RoiSelRect, Math.Min(_dragStart.X, cur.X));
        Canvas.SetTop(RoiSelRect, Math.Min(_dragStart.Y, cur.Y));
        RoiSelRect.Width = Math.Abs(cur.X - _dragStart.X);
        RoiSelRect.Height = Math.Abs(cur.Y - _dragStart.Y);

        // 拖的時候就把換算後的像素座標顯示出來，放開才知道框到哪太慢了
        var p1 = DisplayToPixel(_dragStart);
        var p2 = DisplayToPixel(cur);
        if (ImgLive.Source is BitmapSource b)
        {
            SetCoordText(
                (int)Math.Min(p1.X, p2.X), (int)Math.Min(p1.Y, p2.Y),
                (int)Math.Abs(p2.X - p1.X), (int)Math.Abs(p2.Y - p1.Y),
                b.PixelWidth, b.PixelHeight);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        HostBorder.ReleaseMouseCapture();
        RoiSelRect.Visibility = Visibility.Collapsed;

        var p1 = DisplayToPixel(_dragStart);
        var p2 = DisplayToPixel(e.GetPosition(ViewHost));
        int x = (int)Math.Min(p1.X, p2.X);
        int y = (int)Math.Min(p1.Y, p2.Y);
        int w = (int)Math.Abs(p2.X - p1.X);
        int h = (int)Math.Abs(p2.Y - p1.Y);

        CancelPick();

        // 太小多半是誤觸（點一下就放開）。直接套用會讓 ROI 變成一個小點、整條產線讀不到東西。
        if (w < 50 || h < 50)
        {
            Notice?.Invoke(this,
                $"框選範圍太小（{w}×{h}px，需 ≥50）已取消，ROI 維持原值。");
            return;
        }

        if (ImgLive.Source is BitmapSource bmp && _roi is not null)
        {
            _roi.SetFromPixels(x, y, w, h, bmp.PixelWidth, bmp.PixelHeight);
            Notice?.Invoke(this, $"✓ ROI 已更新並存檔：{x}, {y}, {w}×{h} px（存檔即生效，免重啟）");
            RoiChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdateOverlay();
    }
}
