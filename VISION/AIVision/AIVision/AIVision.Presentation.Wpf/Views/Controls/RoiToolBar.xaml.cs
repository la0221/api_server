using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AIVision.Presentation.Wpf.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace AIVision.Presentation.Wpf.Views.Controls;

/// <summary>
/// ROI 判定區的**唯一**操作介面：自訂範圍、恢復預設、顯示 ROI 框、顯示矩形參數，
/// 外加「目前生效值」一行。功能對齊已驗證的 <c>模號檢驗/相機版</c>（那邊放在「功能」選單裡）。
///
/// <para><b>為什麼包成一個控制項</b>：ROI 的操作以前散在各個畫面各寫一次，
/// 結果是主頁只看得到框、面板只有三顆按鈕、矩形參數沒有人做。
/// 包起來之後每個畫面就兩行 XAML ＋ 一行 <see cref="Attach"/>，不會再長出第二套。</para>
/// </summary>
public partial class RoiToolBar : UserControl
{
    private RoiPreview? _preview;
    private readonly DispatcherTimer _noticeTimer;

    public RoiToolBar()
    {
        InitializeComponent();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            TxtNotice.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>要寫進畫面執行紀錄的訊息（含 <see cref="RoiPreview"/> 轉過來的）。</summary>
    public event EventHandler<string>? Notice;

    /// <summary>ROI 已被改動並存檔（框選完成或重設）。</summary>
    public event EventHandler? RoiChanged;

    // ── 配色（同一個控制項要能放在白底面板，也能放在黑底預覽區）──────────

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(RoiToolBar),
            new PropertyMetadata(FromHex("#FF444444")));

    public static readonly DependencyProperty ValueBrushProperty =
        DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(RoiToolBar),
            new PropertyMetadata(FromHex("#FF2D6CDF")));

    public static readonly DependencyProperty HintBrushProperty =
        DependencyProperty.Register(nameof(HintBrush), typeof(Brush), typeof(RoiToolBar),
            new PropertyMetadata(FromHex("#FF999999")));

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public Brush HintBrush
    {
        get => (Brush)GetValue(HintBrushProperty);
        set => SetValue(HintBrushProperty, value);
    }

    /// <summary>放在黑底預覽區時設 true（只改配色，功能完全一樣）。</summary>
    public bool UseDarkTheme
    {
        get => _dark;
        set
        {
            _dark = value;
            LabelBrush = FromHex(value ? "#FFE0E0E0" : "#FF444444");
            ValueBrush = FromHex(value ? "#FFFFC400" : "#FF2D6CDF");
            HintBrush = FromHex(value ? "#FF9AA5B1" : "#FF999999");
        }
    }

    private bool _dark;

    /// <summary>說明文字要不要顯示（版面很擠時可關）。</summary>
    public bool ShowHelp
    {
        get => TxtHelp.Visibility == Visibility.Visible;
        set => TxtHelp.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>標題文字（預設「ROI 判定區（＝送去辨識的範圍）」）。</summary>
    public string Title
    {
        get => TxtTitle.Text;
        set => TxtTitle.Text = value;
    }

    /// <summary>
    /// 接上要操作的預覽控制項與 ROI 設定。**每個用到 ROI 的畫面只要呼叫這一行。**
    /// </summary>
    public void Attach(RoiPreview preview, RoiSettings roi)
    {
        if (_preview is not null)
        {
            _preview.Notice -= OnPreviewNotice;
            _preview.RoiChanged -= OnPreviewRoiChanged;
            _preview.DescriptionChanged -= OnDescriptionChanged;
        }

        _preview = preview;
        preview.Attach(roi);
        preview.ShowRoi = ChkShow.IsChecked == true;
        preview.ShowCoords = ChkCoords.IsChecked == true;
        preview.Notice += OnPreviewNotice;
        preview.RoiChanged += OnPreviewRoiChanged;
        preview.DescriptionChanged += OnDescriptionChanged;
        RefreshCurrent();
    }

    private void OnPreviewNotice(object? sender, string msg) => Raise(msg);

    /// <summary>訊息同時「顯示在這一組裡」與「丟給宿主畫面的執行紀錄」。
    /// 主頁沒有紀錄面板，只靠事件的話按了按鈕會完全沒有回饋。</summary>
    private void Raise(string msg)
    {
        TxtNotice.Text = msg;
        TxtNotice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        Notice?.Invoke(this, msg);
    }

    private void OnPreviewRoiChanged(object? sender, EventArgs e)
    {
        RefreshCurrent();
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDescriptionChanged(object? sender, EventArgs e) => RefreshCurrent();

    private void RefreshCurrent()
    {
        var d = _preview?.Description;
        TxtCurrent.Text = string.IsNullOrEmpty(d) ? "目前 ROI：（等待影像）" : $"目前 {d}";
    }

    private void OnPick(object sender, RoutedEventArgs e) => _preview?.BeginPick();

    private void OnReset(object sender, RoutedEventArgs e) => _preview?.ResetRoi();

    private void OnToggleShow(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        _preview.ShowRoi = ChkShow.IsChecked == true;
        Raise(_preview.ShowRoi ? "已顯示 ROI 框。" : "已隱藏 ROI 框（判定範圍不變，仍照這組值裁切）。");
    }

    private void OnToggleCoords(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        _preview.ShowCoords = ChkCoords.IsChecked == true;
    }

    private static Brush FromHex(string hex)
    {
        try
        {
            var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            return Brushes.Gray;
        }
    }
}
