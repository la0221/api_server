using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIVision.Domain.Shared;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Utilities;
using AIVision.Presentation.Wpf.Services.Realtime;
using OpenCvSharp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>一張預覽影像（給 View 的 ROI 疊層用；疊層要 BitmapSource 才算得出座標）。</summary>
public sealed class PreviewFrameEventArgs : EventArgs
{
    public BitmapSource? Image { get; init; }
    /// <summary>沒有影像時要顯示的原因。</summary>
    public string? Hint { get; init; }
}

/// <summary>
/// 「模號穴號實時檢測」頁。畫面只做兩件事：**把帳攤開**、**把現在走哪條路講清楚**。
///
/// <para>五個數字要能相加（觸發 = 中央 + 本機 + 擷取失誤 + 待補）；
/// 不平時直接紅字，因為那代表有一條路沒記帳。</para>
/// </summary>
public partial class RealtimeInspectionViewModel : ObservableObject, IDisposable
{
    private readonly RealtimeInspectionPipeline _pipeline;
    private readonly RealtimeEventLog _eventLog;
    /// <summary>目前工單。預期模號/穴號要從這裡來，不能讓現場自己 key（key 錯就整批誤判）。</summary>
    private readonly AIVision.Application.Services.IWorkOrderManagementService? _workOrders;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<RealtimeInspectionViewModel>? _logger;
    private readonly DispatcherTimer _timer;

    public RealtimeInspectionViewModel(
        RealtimeInspectionPipeline pipeline,
        RealtimeEventLog eventLog,
        AIVision.Application.Services.IWorkOrderManagementService? workOrders = null,
        ILogger<RealtimeInspectionViewModel>? logger = null)
    {
        _pipeline = pipeline;
        _eventLog = eventLog;
        _workOrders = workOrders;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        StationId = pipeline.Options.StationId;
        CaptureWindowMs = pipeline.Options.CaptureWindowMs;
        ServerBudgetMs = pipeline.Options.ServerBudgetMs;
        RecordRootText = pipeline.Options.ResolvedRecordRoot;
        LogPathText = RealtimeEventLog.ResolvePath(DateTime.Now);

        _pipeline.LatestFrame += OnLatestFrame;
        _pipeline.PieceCompleted += OnPieceCompleted;
        _pipeline.CaptureFault += OnCaptureFault;
        _pipeline.DiskAnnouncement += OnDisk;
        _pipeline.Log += OnLog;

        // 帳目每秒刷一次。用計時器而不是每片刷：產線一分鐘幾百片，逐片更新會把 UI 執行緒吃光。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshLedger();
        _timer.Start();
    }

    /// <summary>最近幾片（新的在最上）。</summary>
    public ObservableCollection<RealtimePieceRow> Recent { get; } = new();

    /// <summary>執行紀錄（新的在最上）。</summary>
    public ObservableCollection<string> Messages { get; } = new();

    [ObservableProperty] private string _stationId = "ST-01";
    [ObservableProperty] private int _captureWindowMs = 800;
    [ObservableProperty] private int _serverBudgetMs = 100;

    [ObservableProperty] private string? _workOrder;
    [ObservableProperty] private string? _expectedMohao;
    [ObservableProperty] private string? _expectedXuehao;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "尚未啟動";

    // ── 五個數字（要能相加）──
    [ObservableProperty] private int _triggeredCount;
    [ObservableProperty] private int _centralCount;
    [ObservableProperty] private int _localCount;
    [ObservableProperty] private int _captureFaultCount;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _droppedCount;
    [ObservableProperty] private string _ledgerText = "-";
    [ObservableProperty] private bool _ledgerBalanced = true;

    /// <summary>SRV：true＝走中央、false＝本機接管中。</summary>
    [ObservableProperty] private bool _centralOnline = true;

    [ObservableProperty] private string _diskText = "-";
    [ObservableProperty] private int _diskLevel;   // 0=Ok 1=Warning 2=Critical

    [ObservableProperty] private string _recordRootText = "";

    [ObservableProperty] private string _logPathText = "";

    // ── 實時畫面 ──
    /// <summary>相機即時影像（已畫上 ROI 框）。現場靠它確認相機在動、工件有沒有進框。</summary>
    [ObservableProperty] private BitmapSource? _liveImage;

    /// <summary>沒有影像時要講原因，不要只給一片黑（8/19 踩過）。</summary>
    [ObservableProperty] private string _livePreviewHint = "尚未啟動";

    /// <summary>最近一片命中的原圖與前處理圖（對照用：看前處理有沒有裁對）。</summary>
    [ObservableProperty] private BitmapSource? _lastRawImage;
    [ObservableProperty] private BitmapSource? _lastStripImage;

    /// <summary>相機 IO 觸發線讀不讀得到。false＝現場按開關沒反應，只能手動觸發。</summary>
    [ObservableProperty] private bool _triggerLineReady;
    [ObservableProperty] private string _triggerLineText = "-";

    /// <summary>預期值是不是從目前工單帶進來的。false＝人工填的，要提醒。</summary>
    [ObservableProperty] private bool _expectedFromWorkOrder;
    [ObservableProperty] private string _workOrderHint = "尚未載入工單";

    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        try
        {
            _pipeline.Options.StationId = StationId;
            _pipeline.Options.CaptureWindowMs = CaptureWindowMs;
            _pipeline.Options.ServerBudgetMs = ServerBudgetMs;
            // 沒有預期值就判不出混料 —— 這種狀態下跑產線等於沒有在檢查，一定要擋
            if (string.IsNullOrWhiteSpace(ExpectedMohao) || string.IsNullOrWhiteSpace(ExpectedXuehao))
            {
                StatusText = "✗ 沒有預期模號/穴號 —— 判不出混料。請按「帶入工單」或自行填寫後再開始。";
                Push(StatusText);
                return;
            }
            if (!ExpectedFromWorkOrder)
                Push("⚠ 預期值是人工填的，不是從工單帶入的 —— 請再確認一次與工單一致。");

            _pipeline.WorkOrder = WorkOrder;
            _pipeline.ExpectedMohao = ExpectedMohao;
            _pipeline.ExpectedXuehao = ExpectedXuehao;

            await _pipeline.StartAsync(CancellationToken.None);

            // Normalize 可能夾過值，寫回畫面讓現場看到真正生效的數字
            CaptureWindowMs = _pipeline.Options.CaptureWindowMs;
            ServerBudgetMs = _pipeline.Options.ServerBudgetMs;
            StationId = _pipeline.Options.StationId;

            IsRunning = true;
            TriggerLineReady = _pipeline.TriggerLineReady;
            TriggerLineText = TriggerLineReady
                ? $"IO 觸發線 {_pipeline.TriggerLineName} 就緒 —— 按現場開關即觸發"
                : "⚠ 讀不到相機 IO 觸發線 —— **現場按開關不會有反應**，請用「手動觸發」";
            LivePreviewHint = "等待相機影像…";
            StatusText = "運行中，等待觸發…";
            LogPathText = RealtimeEventLog.ResolvePath(DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Realtime] 啟動失敗");
            StatusText = $"啟動失敗：{ex.Message}";
            Push($"✗ 啟動失敗：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning) return;
        await _pipeline.StopAsync();
        IsRunning = false;
        LiveImage = null;
        LivePreviewHint = "已停止";
        StatusText = "已停止。" + _pipeline.Triggers.Ledger;
        RefreshLedger();
    }

    /// <summary>
    /// 從**目前工單**帶入工單號與預期模號/穴號。
    /// <para>不接工單的話，現場得自己在畫面上 key 預期值 —— key 錯就是整批誤判／誤吹，
    /// 而且與實際工單不同步時沒有任何地方看得出來。</para>
    /// </summary>
    [RelayCommand]
    private async Task LoadWorkOrderAsync()
    {
        if (_workOrders is null)
        {
            WorkOrderHint = "⚠ 沒有工單服務可用，只能人工填預期值";
            ExpectedFromWorkOrder = false;
            return;
        }
        try
        {
            var wo = await _workOrders.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (wo is null)
            {
                WorkOrderHint = "⚠ 目前沒有進行中的工單 —— 請先在「工單管理」開工單，或自行填預期值";
                ExpectedFromWorkOrder = false;
                return;
            }

            WorkOrder = wo.Code;

            // 工單存的是一個字串（例如 M101/02 或 M101-02），拆成兩軸。
            // 拆法與「工單輸入」頁一致，避免兩處規則不同而對不起來。
            var raw = wo.ExpectedMoldCode;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split(new[] { '/', '-', '_', ' ' }, 2,
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    ExpectedMohao = parts[0];
                    ExpectedXuehao = parts[1];
                    ExpectedFromWorkOrder = true;
                    WorkOrderHint = $"✓ 已從工單 {wo.Code} 帶入預期值 {ExpectedMohao}/{ExpectedXuehao}";
                    return;
                }
                WorkOrderHint = $"⚠ 工單 {wo.Code} 的預期值「{raw}」拆不出模號/穴號，請確認格式（例：M101/02）";
            }
            else
            {
                WorkOrderHint = $"⚠ 工單 {wo.Code} 沒有填預期模號/穴號 —— 沒有預期值就判不出混料";
            }
            ExpectedFromWorkOrder = false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Realtime] 載入工單失敗");
            WorkOrderHint = $"⚠ 載入工單失敗：{ex.Message}";
            ExpectedFromWorkOrder = false;
        }
    }

    /// <summary>手動觸發一次（現場沒接 IO 或要驗流程時用）。</summary>
    [RelayCommand]
    private void ManualTrigger() => _pipeline.FireTrigger("手動");

    [RelayCommand]
    private void ClearMessages() => Messages.Clear();

    private void RefreshLedger()
    {
        var t = _pipeline.Triggers;
        TriggeredCount = t.Triggered;
        CentralCount = t.Central;
        LocalCount = t.Local;
        CaptureFaultCount = t.CaptureFault;
        PendingCount = t.Pending;
        DroppedCount = t.Dropped;
        LedgerText = t.Ledger;
        LedgerBalanced = t.Balanced;
        CentralOnline = _pipeline.CentralOnline;
    }

    /// <summary>
    /// 相機每一幀都會進來（幾十 fps）。**一定要節流**：每幀都轉 BitmapSource 丟給 UI 執行緒，
    /// 會把 UI 執行緒吃光、連帶拖慢檢測迴圈。這裡只在「上一張畫完了」時才處理下一張。
    /// </summary>
    private void OnLatestFrame(object? sender, ImageData image)
    {
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        try
        {
            var bmp = RenderPreview(image);
            _lastFrameW = image.Width; _lastFrameH = image.Height;
            _dispatcher.BeginInvoke(() =>
            {
                LiveImage = bmp;
                LivePreviewHint = bmp is null ? "影像解碼失敗" : string.Empty;
                PreviewUpdated?.Invoke(this, new PreviewFrameEventArgs
                {
                    Image = bmp,
                    Hint = bmp is null ? "影像解碼失敗" : null,
                });
                Interlocked.Exchange(ref _renderPending, 0);
            });
        }
        catch
        {
            Interlocked.Exchange(ref _renderPending, 0);
        }
    }

    private int _renderPending;
    private int _lastFrameW, _lastFrameH;

    /// <summary>預覽影像更新（View 拿去餵 ROI 疊層）。</summary>
    public event EventHandler<PreviewFrameEventArgs>? PreviewUpdated;

    /// <summary>給 View 推訊息進執行紀錄。</summary>
    public void PushMessage(string msg) => _dispatcher.BeginInvoke(() => Push(msg));

    /// <summary>
    /// 把相機幀轉成可顯示的點陣圖。
    /// <para>⚠ **不在這裡畫 ROI 框**：框由 View 的 Canvas 疊層畫。
    /// 早期版本是用 OpenCV 把框畫進圖裡再重編碼成 PNG —— 1280x1024 一張要 10~30ms，
    /// 每顯示一幀就付一次，而且那樣也沒辦法做拖曳框選。</para>
    /// </summary>
    private static BitmapSource? RenderPreview(ImageData image)
    {
        try
        {
            var bmp = BitmapSourceFactory.FromImageData(image);
            // ★ 必須 Freeze：這裡跑在相機的背景執行緒，但影像會 BeginInvoke 丟給 UI 執行緒用。
            // 沒凍結的 BitmapSource 綁定建立它的執行緒，UI 一讀 PixelWidth 就拋
            // InvalidOperationException「呼叫執行緒無法存取此物件」→ 整個 App 當掉。
            // （2026-08-25 實機踩到：一按「開始」就崩在 RoiPreview.UpdateOverlay。）
            if (bmp is not null && bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void OnPieceCompleted(object? sender, PieceCompletedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Recent.Insert(0, RealtimePieceRow.From(e.Record));
            if (e.StripPng is { Length: > 0 }) LastStripImage = Decode(e.StripPng);
            if (e.RawJpeg is { Length: > 0 }) LastRawImage = Decode(e.RawJpeg);
            while (Recent.Count > _pipeline.Options.RecentCapacity)
                Recent.RemoveAt(Recent.Count - 1);
            RefreshLedger();
        });
    }

    private void OnCaptureFault(object? sender, CaptureFaultEventArgs e) =>
        _dispatcher.BeginInvoke(RefreshLedger);

    private void OnDisk(object? sender, DiskStatus st) =>
        _dispatcher.BeginInvoke(() =>
        {
            DiskText = st.Text;
            DiskLevel = (int)st.Level;
        });

    private void OnLog(object? sender, string msg) => _dispatcher.BeginInvoke(() => Push(msg));

    private void Push(string msg)
    {
        Messages.Insert(0, $"{DateTime.Now:HH:mm:ss}　{msg}");
        while (Messages.Count > 300) Messages.RemoveAt(Messages.Count - 1);
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _pipeline.LatestFrame -= OnLatestFrame;
        _pipeline.PieceCompleted -= OnPieceCompleted;
        _pipeline.CaptureFault -= OnCaptureFault;
        _pipeline.DiskAnnouncement -= OnDisk;
        _pipeline.Log -= OnLog;
    }
}

/// <summary>畫面上「最近幾片」的一列。</summary>
public sealed class RealtimePieceRow
{
    public string PieceId { get; init; } = "";
    public string Time { get; init; } = "";
    public string Reading { get; init; } = "";
    public string Expected { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string Source { get; init; } = "";
    public string Conf { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string Elapsed { get; init; } = "";
    public string Blow { get; init; } = "";
    public bool IsReject { get; init; }
    public bool IsLocal { get; init; }

    public static RealtimePieceRow From(PieceRecord r) => new()
    {
        PieceId = r.PieceId,
        Time = r.Timestamp.ToString("HH:mm:ss"),
        Reading = r.ReadingText,
        Expected = $"{r.ExpectedMohao}/{r.ExpectedXuehao}",
        Outcome = r.Outcome switch
        {
            "Match" => "✓ 相符",
            "TrustInput" => "採信輸入",
            "MixedAlarm" => "⚠ 混料",
            "Reject" => "✗ NG",
            _ => "—",
        },
        Source = r.Source == "central" ? "中央" : "本機",
        Conf = $"{r.ConfMohao:0.00}/{r.ConfXuehao:0.00}",
        ModelVersion = r.ModelVersion ?? "-",
        Elapsed = r.ServerMs > 0 ? $"{r.ServerMs} ms" : $"{r.ElapsedMs:0} ms",
        Blow = r.Blown ? (r.BlowElapsedFromTriggerMs is long ms ? $"吹（觸發後 {ms}ms）" : "吹") : "—",
        IsReject = r.Outcome is "MixedAlarm" or "Reject",
        IsLocal = r.Source != "central",
    };

    /// <summary>給自動化／朗讀程式看的名稱；不覆寫的話它們只會讀到型別名。</summary>
    public override string ToString() => $"{Time}　{PieceId}　{Reading}　{Outcome}";
}
