using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Application.Ports.MoldCode;
using AIVision.Infrastructure.MoldCode;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 「站端送檢（前處理下放）」頁——把 2026-08-14 跨機實測通過的資料流做進主程式的 edge 端。
/// <para>
/// 流程：原圖留本機 → 本機做<b>與模型相同的前處理</b>（<see cref="WarpPolarPreprocessor.Preprocess"/>，
/// 找圓→裁圓→極座標展開→640 白底）→ <b>只把小圖送中央推論</b> → 收讀值 → 逐筆留紀錄可回溯。
/// </para>
/// <para>
/// 實測效益（30 張、實體網路線同網段）：讀值 30/30 與送原圖一致、傳輸量 −68.6%（100KB→31KB）、
/// 端到端 p50 83.7ms。前處理參數與 python 端一致（RInner=0.6 / Imgsz=640 / PadValue=255）。
/// </para>
/// <para>⚠ server 不可達時走本機備援標記（不停線）；此頁為骨架版，先驗畫面與流程。</para>
/// </summary>
public partial class RouteAEdgeViewModel : ObservableObject
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private readonly CrnnInferClient _client;
    private readonly IFolderPickerPort _folderPicker;
    /// <summary>本機辨識器（雙 head ONNX）——中央掉線時由它接管，產線照跑。</summary>
    private readonly IMoldCodePairRecognizerPort _localRecognizer;
    /// <summary>送檢事件記錄檔——畫面上的數字關窗就沒了，驗收要靠這份檔自動回填（需求 1）。</summary>
    private readonly RouteAEventLog _eventLog;
    private readonly InferenceServerOptions _serverOptions;
    private readonly ILogger<RouteAEdgeViewModel>? _logger;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 中央不可達時的冷卻期：連不上時每張都要等 TCP 逾時（實測 ~4 秒/張），產線節拍會直接爛掉。
    /// 因此第一張確認打不通後，這段期間內**直接走本機備援不再重試**，冷卻到期才再試一次。
    /// </summary>
    private static readonly TimeSpan ServerDownCooldown = TimeSpan.FromSeconds(30);
    private DateTime _serverDownUntil = DateTime.MinValue;

    public RouteAEdgeViewModel(
        CrnnInferClient client,
        IFolderPickerPort folderPicker,
        IMoldCodePairRecognizerPort localRecognizer,
        RouteAEventLog eventLog,
        IOptions<InferenceServerOptions> serverOptions,
        ILogger<RouteAEdgeViewModel>? logger = null)
    {
        _client = client;
        _folderPicker = folderPicker;
        _localRecognizer = localRecognizer;
        _eventLog = eventLog;
        _serverOptions = serverOptions.Value;
        _logger = logger;
        LogPathText = RouteAEventLog.ResolvePath(DateTime.Now);   // 還沒跑就先讓現場知道檔案會在哪
    }

    /// <summary>逐張結果（新的在最前）。</summary>
    public ObservableCollection<RouteAEdgeItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _sourceFolder = string.Empty;
    [ObservableProperty] private string _stationId = "ST-01";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "尚未開始";

    /// <summary>前處理成功（找到圓）張數；找不到圓會退回整張送。</summary>
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _serverOkCount;
    [ObservableProperty] private int _fallbackCount;

    /// <summary>
    /// **擷取失誤**的張數（目前只有「找不到圓」一種）。
    /// <para>不是不良品——產線上找不到圓代表**重複觸發拍照**造成擷取失誤（工件還沒進框、
    /// 或前一片已走掉）。沒有壞件可吹，該做的是去查觸發。</para>
    /// <para>單獨一格，統計才加得起來：這種張數既沒送中央、也不是本機接管，
    /// 混進任何一邊都會讓驗收數字說謊；混進 NG 更會讓人以為是品質問題。</para>
    /// </summary>
    [ObservableProperty] private int _captureFaultCount;
    /// <summary>本機接管且真的讀出值的張數（fallback 之中有多少是有效讀值）。</summary>
    [ObservableProperty] private int _localReadCount;
    [ObservableProperty] private double _rawKb;
    [ObservableProperty] private double _sentKb;
    [ObservableProperty] private string _reductionText = "-";
    [ObservableProperty] private string _latencyText = "-";

    /// <summary>最近一張的原圖與前處理圖（畫面左側對照用）。</summary>
    [ObservableProperty] private BitmapSource? _lastRawImage;
    [ObservableProperty] private BitmapSource? _lastStripImage;

    /// <summary>事件記錄檔位置（現場要知道去哪撈驗收數據）。</summary>
    [ObservableProperty] private string _logPathText = "尚未寫入";

    public bool CanStart => !IsRunning && Directory.Exists(SourceFolder);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnSourceFolderChanged(string value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var picked = await _folderPicker.PickFolderAsync(CancellationToken.None).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked)) SourceFolder = picked!;
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        TotalCount = ServerOkCount = FallbackCount = LocalReadCount = CaptureFaultCount = 0;
        RawKb = SentKb = 0;
        ReductionText = LatencyText = "-";
        LastRawImage = LastStripImage = null;
        StatusText = "已清除";
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        StatusText = "已要求停止…";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        if (!Directory.Exists(SourceFolder))
        {
            StatusText = "請先選擇影像資料夾";
            return;
        }

        var files = Directory.EnumerateFiles(SourceFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => ImageExt.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            StatusText = "此資料夾沒有影像";
            return;
        }

        IsRunning = true;
        _serverDownUntil = DateTime.MinValue;   // 新的一批：重新給中央一次機會
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var latencies = new List<double>();
        var pars = new WarpPolarParams();

        // 這一批的起算點（統計卡是累加的，事件檔要記「這批」的量）
        var baseTotal = TotalCount;
        var baseServerOk = ServerOkCount;
        var baseFallback = FallbackCount;
        var baseLocalRead = LocalReadCount;
        var baseFault = CaptureFaultCount;
        var baseRawKb = RawKb;
        var baseSentKb = SentKb;

        _eventLog.BatchStart(StationId, SourceFolder, files.Count,
            string.IsNullOrWhiteSpace(_serverOptions.BaseUrl) ? "(未設定)" : _serverOptions.BaseUrl!);

        try
        {
            StatusText = $"送檢中… 共 {files.Count} 張";
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                await ProcessOneAsync(file, pars, latencies, ct).ConfigureAwait(true);
            }

            var ngText = CaptureFaultCount > 0
                ? $"，⚠ 擷取失誤 {CaptureFaultCount}（找不到圓——多半是重複觸發，請查觸發訊號）"
                : "";
            StatusText = ct.IsCancellationRequested
                ? $"已停止（完成 {TotalCount}/{files.Count}）{ngText}"
                : $"完成：{TotalCount} 張，送達 {ServerOkCount}，本機備援 {FallbackCount}{ngText}";

            // ⚠ 判斷「中央是不是真的不可達」只能看**嘗試送過的**那些張。
            // 用 ServerOkCount == 0 && TotalCount > 0 會把「整批都是 NG（根本沒送）」誤報成網路故障
            // ——那正是 2026-08-24 實測踩到的那種假警報。
            if (ServerOkCount == 0 && FallbackCount > 0)
                StatusText = $"⚠ 中央推論不可達，全部由本機接管（本機讀出 {LocalReadCount}/{FallbackCount}）——請檢查伺服器位址與連線{ngText}";
            else if (FallbackCount > 0)
                StatusText += $"（其中 {FallbackCount} 張由本機接管，讀出 {LocalReadCount}）";
        }
        finally
        {
            // 批次統計落地——驗收表的數字從這一行整段複製即可，不必看畫面（需求 1 驗收條件）。
            _eventLog.BatchEnd(
                StationId,
                TotalCount - baseTotal,
                ServerOkCount - baseServerOk,
                FallbackCount - baseFallback,
                LocalReadCount - baseLocalRead,
                RawKb - baseRawKb,
                SentKb - baseSentKb,
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.90),
                ct.IsCancellationRequested,
                CaptureFaultCount - baseFault);
            LogPathText = _eventLog.CurrentPath ?? "（寫入失敗，見 AIVision log）";

            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>百分位（線性內插；空清單回 null）。p50 給畫面、p50/p90 一起進事件檔。</summary>
    private static double? Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 1) return sorted[0];
        var pos = q * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>單張：讀檔 → 前處理 → 送出 → 記錄。任何一步失敗都不中斷整批。</summary>
    private async Task ProcessOneAsync(
        string file, WarpPolarParams pars, List<double> latencies, CancellationToken ct)
    {
        var item = new RouteAEdgeItemViewModel
        {
            Index = TotalCount + 1,
            FileName = Path.GetFileName(file),
            RawPath = file,
        };

        byte[] rawBytes;
        try { rawBytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(true); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[RouteA] 讀檔失敗 {File}", file);
            _eventLog.ItemFailed(StationId, item.Index, item.FileName, file, "讀檔失敗");
            return;
        }

        item.RawBytes = rawBytes.Length;

        // ── 前處理：與模型相同（找圓→裁圓→極座標展開→640 白底）
        byte[] sentBytes;
        var noCircle = false;
        try
        {
            using var bgr = Cv2.ImDecode(rawBytes, ImreadModes.Color);
            if (bgr.Empty())
            {
                _logger?.LogWarning("[RouteA] 影像解碼失敗 {File}", file);
                _eventLog.ItemFailed(StationId, item.Index, item.FileName, file, "影像解碼失敗");
                return;
            }

            using var strip = WarpPolarPreprocessor.Preprocess(bgr, 0.0, pars);
            if (strip is null)
            {
                // ── 找不到圓 → 判 NG，直接送 NG 訊號，**不送中央**（2026-08-24 使用者拍板）
                //
                // 舊做法是「退回送原圖，server 端會自己前處理」。那條路其實從來沒通過，而且會連累別人：
                //   原圖是 JPEG，但 client 固定宣告 format=png → 端點依約回 415
                //   → 舊程式把「任何非 2xx」當成中央掛了 → 進 30 秒降級冷卻
                //   → 後面每一張好圖都不再送中央，全部落到本機舊模型
                // 實測（中央全程正常）：送達中央 0 / 本機接管 7，畫面還警告「請檢查伺服器位址與連線」。
                // 看不到圓＝這片有問題（沒鏡片／位置跑掉／破損），本來就該剔除，沒有再送去辨識的必要。
                noCircle = true;
                item.Preprocessed = false;
                sentBytes = Array.Empty<byte>();   // 什麼都沒送出去，傳輸量統計才不會被灌水
            }
            else
            {
                item.Preprocessed = true;
                Cv2.ImEncode(".png", strip, out sentBytes);
                using var visible = CropWhitePadding(strip);
                LastStripImage = ToBitmap(visible);      // 裁掉白邊才看得出字元
            }
            LastRawImage = ToBitmap(bgr);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[RouteA] 前處理失敗 {File}", file);
            _eventLog.ItemFailed(StationId, item.Index, item.FileName, file, "前處理失敗");
            return;
        }

        item.SentBytes = sentBytes.Length;

        // ── 找不到圓 → **擷取失誤**，這張到此為止（不送中央、不吹氣、也不碰降級冷卻）
        //
        // ⚠ 這不是「不良品」，是**系統失誤**（2026-08-24 使用者釐清）：
        //   產線上理論上不會發生；真的發生代表**重複觸發拍照**導致擷取到不該擷取的畫面
        //   （工件還沒進框、或前一片已經走掉）。
        //   → 沒有壞件可以吹，吹了只是對空氣噴氣、還可能誤傷下一片。該做的是去查為什麼重複觸發。
        //   相機版的做法一致：這種幀直接跳過繼續找，窗到期就記進 _abandonedTriggers（誤觸/卡料/模具沒進 ROI），
        //   全程不吹。
        //
        // 也不能混進良品或 NG 的統計 —— 它要單獨看得見，數字變多就是現場有觸發問題。
        if (noCircle)
        {
            item.Source = "擷取失誤（找不到圓）";
            item.Reading = "(找不到圓)";
            item.IsOk = false;
            CaptureFaultCount++;
            FinishItem(item, sentBytes, rawBytes, latencies, sourceTag: "capture_fault_no_circle");
            return;
        }

        // ── 送中央推論（冷卻期內不再嘗試，避免每張空等 TCP 逾時拖垮節拍）
        var sw = Stopwatch.StartNew();
        CrnnInferDto? dto;
        var skipped = DateTime.Now < _serverDownUntil;
        if (skipped)
        {
            dto = null;
        }
        else
        {
            // item.Preprocessed=true 代表送出的是展開好的 strip → 告訴父端「只做辨識」。
            // 帶上原圖在站端的位置：父端「最近辨識紀錄」才回得出「這張的原圖在哪」（溯源）。
            dto = await _client.RecognizeAsync(sentBytes, StationId, ct, null, item.Preprocessed, file)
                .ConfigureAwait(true);
            if (dto is null)
            {
                // ⚠ 只有「對方不可用」才進冷卻（連不上／逾時／5xx）。
                // 4xx 是我方請求有問題，把它當成中央掛了會讓後面一整批好圖被連坐降級
                // （2026-08-24 UI 實測：一張圖的 415 害後面 6 張全部沒送到中央）。
                if (_client.LastFailureWasServerSide)
                    _serverDownUntil = DateTime.Now.Add(ServerDownCooldown);
                else
                    _logger?.LogWarning(
                        "[RouteA] 中央拒收此張（HTTP {Status}）——我方請求有問題，不視為中央掉線，後續影像照常送 {File}",
                        _client.LastStatusCode, item.FileName);
            }
            else
            {
                // 冷卻期滿後這次試通了 → 切回中央
                if (_serverDownUntil != DateTime.MinValue)
                    StatusText = "中央推論已恢復，切回中央辨識";
                _serverDownUntil = DateTime.MinValue;
            }
        }
        sw.Stop();
        item.ElapsedMs = sw.Elapsed.TotalMilliseconds;

        if (dto is null)
        {
            // ── 中央不可達 → **本機模型接管**（雙 head ONNX，產線照跑不停線）
            FallbackCount++;
            item.Source = skipped ? "本機接管" : "本機接管(中央剛掉線)";
            try
            {
                var image = MoldCodeImageLoader.LoadFromBytes(rawBytes);   // 本機辨識器吃原圖，自己前處理
                var obs = _localRecognizer.Recognize(image);
                if (obs.HasReading)
                {
                    item.Reading = $"{obs.Mohao}/{obs.Xuehao}";
                    item.IsOk = true;
                    LocalReadCount++;
                }
                else
                {
                    item.Reading = obs.ObjectPresent ? "(本機讀不到)" : "(無鏡片)";
                    item.IsOk = false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[RouteA] 本機辨識失敗 {File}", file);
                item.Reading = "(本機辨識失敗)";
                item.IsOk = false;
            }
        }
        else
        {
            item.Source = "中央推論";
            item.Reading = string.IsNullOrWhiteSpace(dto.Mohao) ? "(無讀值)" : $"{dto.Mohao}/{dto.Xuehao}";
            item.NeedsReview = dto.NeedsReview;
            item.IsOk = dto.HasReading;
            item.ServerMs = dto.ElapsedMs;
            ServerOkCount++;
            latencies.Add(item.ElapsedMs);
        }

        FinishItem(item, sentBytes, rawBytes, latencies, dto is null ? "local" : "central");
    }

    /// <summary>統計、落地、上表——每張的收尾都走這裡，免得有分支忘了記帳。</summary>
    private void FinishItem(
        RouteAEdgeItemViewModel item, byte[] sentBytes, byte[] rawBytes,
        List<double> latencies, string sourceTag)
    {
        TotalCount++;
        RawKb += rawBytes.Length / 1000.0;
        SentKb += sentBytes.Length / 1000.0;
        ReductionText = RawKb > 0 ? $"−{(1 - SentKb / RawKb) * 100:F1}%" : "-";
        if (latencies.Count > 0)
        {
            var sorted = latencies.OrderBy(x => x).ToList();
            LatencyText = $"{sorted[sorted.Count / 2]:F0} ms";
        }

        // 逐張落地（append-only）：讀值與傳輸量只存在畫面記憶體裡的話，關窗就撈不回來了。
        _eventLog.Item(
            StationId, item.Index, item.FileName, item.RawPath, item.Reading,
            sourceTag,
            item.IsOk, item.NeedsReview, item.Preprocessed,
            item.RawBytes, item.SentBytes, item.ElapsedMs, item.ServerMs);

        Items.Insert(0, item);
        while (Items.Count > 200) Items.RemoveAt(Items.Count - 1);   // 保護記憶體
    }


    /// <summary>
    /// 顯示用：strip 是 640×640 的白底 letterbox，實際內容只佔中間一條窄帶——
    /// 直接縮圖會糊成一條線、現場無法用肉眼確認前處理對不對。這裡裁掉上下白邊只留內容。
    /// 純顯示用途，不影響實際送出的位元組。
    /// </summary>
    private static Mat CropWhitePadding(Mat strip)
    {
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(strip, gray, ColorConversionCodes.BGR2GRAY);
            using var mask = new Mat();
            Cv2.Threshold(gray, mask, 245, 255, ThresholdTypes.BinaryInv);   // 非白＝內容
            var rect = Cv2.BoundingRect(mask);
            if (rect.Width < 8 || rect.Height < 4) return strip.Clone();      // 找不到內容就原樣

            const int pad = 4;                                               // 留一點邊比較好看
            var x = Math.Max(0, rect.X - pad);
            var y = Math.Max(0, rect.Y - pad);
            var w = Math.Min(strip.Width - x, rect.Width + pad * 2);
            var h = Math.Min(strip.Height - y, rect.Height + pad * 2);
            return new Mat(strip, new Rect(x, y, w, h)).Clone();
        }
        catch
        {
            return strip.Clone();
        }
    }

    private static BitmapSource? ToBitmap(Mat mat)
    {
        try
        {
            Cv2.ImEncode(".png", mat, out var png);
            using var ms = new MemoryStream(png);
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
}

/// <summary>子端逐張結果列。</summary>
public partial class RouteAEdgeItemViewModel : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _rawPath = string.Empty;
    [ObservableProperty] private string _reading = "-";
    [ObservableProperty] private string _source = "-";
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _needsReview;
    [ObservableProperty] private bool _preprocessed;
    [ObservableProperty] private long _rawBytes;
    [ObservableProperty] private long _sentBytes;
    [ObservableProperty] private double _elapsedMs;
    [ObservableProperty] private int _serverMs;

    public string RawKbText => $"{RawBytes / 1000.0:F0} KB";
    public string SentKbText => $"{SentBytes / 1000.0:F0} KB";
    public string CutText => RawBytes > 0 ? $"−{(1 - (double)SentBytes / RawBytes) * 100:F0}%" : "-";
    public string ElapsedText => $"{ElapsedMs:F0} ms";
}
