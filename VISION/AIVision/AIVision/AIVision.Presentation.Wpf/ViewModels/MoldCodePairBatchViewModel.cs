using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Infrastructure.MoldCode;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Services.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 模號 + 穴號「雙 head 模型管理 + 離線測試」頁。
/// 上半：列出 <c>D:\AIVisionModels\pairs\&lt;version&gt;</c>（含 mohao.onnx + xuehao.onnx）的版本，
/// 選一個按「載入」即透過 <see cref="IMoldCodePairModelSwitch"/> 抽換為目前使用的雙 head 模型。
/// 下半：載入後可選一批測試影像，用「目前模型」(<see cref="IMoldCodePairRecognizerPort"/>) 跑辨識、算準確率。
/// </summary>
public partial class MoldCodePairBatchViewModel : ObservableObject
{
    private const string PairsRoot = @"D:\AIVisionModels\pairs";

    private readonly IMoldCodePairModelSwitch _modelSwitch;
    private readonly IMoldCodePairRecognizerPort _recognizer;
    private readonly IFolderPickerPort _folderPicker;
    private readonly INavigationService _navigation;
    private readonly Services.PairWorkflowState _state;
    private readonly RemotePairRecognizer _remoteRecognizer;
    private readonly ModelHubClient _modelHub;
    private readonly InferenceServerOptions _serverOptions;
    private readonly ILogger<MoldCodePairBatchViewModel>? _logger;

    public MoldCodePairBatchViewModel(
        IMoldCodePairModelSwitch modelSwitch,
        IMoldCodePairRecognizerPort recognizer,
        IFolderPickerPort folderPicker,
        INavigationService navigation,
        Services.PairWorkflowState state,
        RemotePairRecognizer remoteRecognizer,
        ModelHubClient modelHub,
        IOptions<InferenceServerOptions> serverOptions,
        IOptions<Models.TestImageFolderOptions>? testFolders = null,
        ILogger<MoldCodePairBatchViewModel>? logger = null)
    {
        _modelSwitch = modelSwitch;
        _recognizer = recognizer;
        _folderPicker = folderPicker;
        _navigation = navigation;
        _state = state;
        _remoteRecognizer = remoteRecognizer;
        _modelHub = modelHub;
        _serverOptions = serverOptions.Value;
        _logger = logger;
        ServerBaseUrl = _serverOptions.BaseUrl;
        TestFolderOptions = new ObservableCollection<string>(
            testFolders?.Value.Paths ?? new List<string>());
        SelectedFolder = state.LastImageFolder;
        AvailableVersions = new ObservableCollection<PairVersionOption>();
        Results = new ObservableCollection<MoldCodePairBatchRow>();
        DiscoverVersions();
        CurrentVersionName = _modelSwitch.CurrentVersionName;
        StatusMessage = string.IsNullOrWhiteSpace(CurrentVersionName)
            ? "步驟①：選版本→「載入為目前模型」。步驟②：選測試影像資料夾→「執行批量辨識」。"
            : $"目前模型版本：{CurrentVersionName}。可選測試影像資料夾後執行批量辨識。";
    }

    // ===== 上半：模型管理 =====

    /// <summary>「測試資料夾」下拉的常用路徑選項（appsettings TestImageFolders；仍可貼任意路徑）。</summary>
    public ObservableCollection<string> TestFolderOptions { get; }

    public ObservableCollection<PairVersionOption> AvailableVersions { get; }

    [ObservableProperty]
    private PairVersionOption? selectedVersion;

    /// <summary>目前已載入（使用中）的版本名稱。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelLoaded))]
    private string? currentVersionName;

    /// <summary>是否已載入模型（控制測試按鈕可用）。</summary>
    public bool HasModelLoaded => !string.IsNullOrWhiteSpace(CurrentVersionName);

    [RelayCommand]
    private void Refresh() => DiscoverVersions();

    // ===== 中央推論（API server）驗收 =====
    // 整合設計書 2026-07-15_edge_server_integration.md 階段2：只驗證「路通不通」，
    // **不碰生產熱迴圈**——上方批量/實機辨識仍走本機 ONNX。驗收通過後才談切換（階段3）。

    /// <summary>中央推論 server 位址（顯示用，來自 appsettings 的 InferenceServer:BaseUrl）。</summary>
    [ObservableProperty]
    private string? serverBaseUrl;

    /// <summary>
    /// 批量辨識來源：false=本機 ONNX（預設）；true=中央伺服器。
    /// 走中央＝隔離試模（ROADMAP 主項2）：不需先載入本地模型、完全不動本地模型狀態。
    /// 只影響本頁批量測試，生產熱迴圈不受影響。
    /// </summary>
    [ObservableProperty]
    private bool useRemoteSource;

    /// <summary>
    /// 指定 server 端模型版本做隔離試模（留空 = server 現用 baseline）。僅來源=中央伺服器時生效。
    /// 新模型只需發布到 server 的登錄夾，edge 不必下載即可整批試——主項2「不污染本地」的完整形。
    /// </summary>
    [ObservableProperty]
    private string? serverModelVersion;

    /// <summary>server 登錄夾版本下拉選項（按「查伺服器版本」載入；也可直接手填）。</summary>
    public ObservableCollection<string> ServerVersionOptions { get; } = new();

    /// <summary>向 server 要版本清單，填入下拉（GET /api/models）。</summary>
    [RelayCommand]
    private async Task FetchServerVersionsAsync()
    {
        StatusMessage = $"查詢伺服器版本清單… {ServerBaseUrl}";
        var list = await _modelHub.ListAsync("ocr_pair");
        if (list is null)
        {
            StatusMessage = $"❌ 拿不到版本清單：{ServerBaseUrl}（server 未啟動或版本過舊沒有 /api/models）。";
            return;
        }
        ServerVersionOptions.Clear();
        foreach (var v in list.Versions)
            if (!string.IsNullOrWhiteSpace(v.Version))
                ServerVersionOptions.Add(v.Version!);
        StatusMessage = $"伺服器有 {ServerVersionOptions.Count} 個版本" +
                        $"（現用 {list.ServerCurrentVersion ?? "—"}）。選一個或留空=現用。";
    }

    /// <summary>驗收結果文字（多行；含健康檢查、讀值、延遲）。</summary>
    [ObservableProperty]
    private string? serverTestResult;

    /// <summary>驗收是否進行中（防連點）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotTestingServer))]
    private bool isTestingServer;

    public bool NotTestingServer => !IsTestingServer;

    /// <summary>
    /// 測試中央推論：① 健康檢查 → ② 送一張測試圖驗讀值/延遲。
    /// 這是「API server 有沒有打通」的示意；失敗會明確指出卡在哪一步。
    /// </summary>
    [RelayCommand]
    private async Task TestCentralInferenceAsync()
    {
        IsTestingServer = true;
        ServerTestResult = $"連線中… {_serverOptions.BaseUrl}";
        try
        {
            // ① 健康檢查（不送圖，便宜）
            var swHealth = Stopwatch.StartNew();
            var health = await _remoteRecognizer.CheckHealthAsync();
            swHealth.Stop();

            if (health is null)
            {
                ServerTestResult =
                    $"❌ 連不上 server：{_serverOptions.BaseUrl}\n" +
                    $"   請確認 API 已啟動、位址/埠正確、防火牆放行。";
                return;
            }

            var healthLine =
                $"✅ 健康檢查 {swHealth.ElapsedMilliseconds}ms｜狀態 {health.Status}" +
                $"｜模型 {health.ModelVersion ?? "（未載入）"}" +
                $"｜類別 {health.MohaoClassCount}/{health.XuehaoClassCount}";

            if (!health.ModelLoaded)
            {
                ServerTestResult =
                    healthLine + "\n" +
                    "⚠ server 活著，但**尚未載入雙 head 模型** → 無法推論。\n" +
                    "   請在 server 的 appsettings 設定 MoldCodeWarpPolar 的 mohao/xuehao .onnx 路徑。";
                return;
            }

            // ② 取一張測試圖（沿用已選的測試影像資料夾）
            var file = string.IsNullOrWhiteSpace(SelectedFolder)
                ? null
                : EnumerateImages(SelectedFolder!).FirstOrDefault();

            if (file is null)
            {
                ServerTestResult =
                    healthLine + "\n" +
                    "⚠ server 已就緒，但尚未選測試影像資料夾（步驟②）→ 只驗到連線，未驗讀值。";
                return;
            }

            var img = await Task.Run(() => MoldCodeImageLoader.LoadFromFile(file));
            var swInfer = Stopwatch.StartNew();
            var obs = await _remoteRecognizer.RecognizeAsync(img);
            swInfer.Stop();

            if (_remoteRecognizer.LastCallFailed)
            {
                ServerTestResult =
                    healthLine + "\n" +
                    $"❌ 推論失敗：{obs.FailureReason}\n" +
                    $"   圖：{Path.GetFileName(file)}（{img.Width}×{img.Height} {img.PixelFormat}）";
                return;
            }

            // 讀不到碼也是「路通了」——那是 server 的有效觀測（fail-closed），非故障。
            var readLine = obs.HasReading
                ? $"✅ 測試讀值 {obs.Mohao} / {obs.Xuehao}｜信心 {obs.ConfMohao:F3} / {obs.ConfXuehao:F3}"
                : $"⚠ 路通了，但這張沒讀到碼（{obs.FailureReason ?? "無讀值"}）——" +
                  $"這是 server 的有效觀測，非故障。可換一張圖再試。";

            ServerTestResult =
                healthLine + "\n" +
                readLine + "\n" +
                $"   圖：{Path.GetFileName(file)}（{img.Width}×{img.Height} {img.PixelFormat}）\n" +
                $"   server 推論 {_remoteRecognizer.LastServerElapsedMs}ms｜來回 {swInfer.ElapsedMilliseconds}ms" +
                $"｜版本 {_remoteRecognizer.LastModelVersion ?? "—"}";

            _logger?.LogInformation(
                "[中央推論驗收] {Health} | 讀值={Mohao}/{Xuehao} | server={ServerMs}ms 來回={WallMs}ms",
                health.Status, obs.Mohao, obs.Xuehao, _remoteRecognizer.LastServerElapsedMs,
                swInfer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            ServerTestResult = $"❌ 測試發生例外：{ex.Message}";
            _logger?.LogWarning(ex, "[中央推論驗收] 例外");
        }
        finally
        {
            IsTestingServer = false;
        }
    }

    private void DiscoverVersions()
    {
        var previousName = SelectedVersion?.Name;
        AvailableVersions.Clear();
        try
        {
            if (Directory.Exists(PairsRoot))
            {
                foreach (var dir in Directory.GetDirectories(PairsRoot).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var mo = Path.Combine(dir, "mohao.onnx");
                    var xu = Path.Combine(dir, "xuehao.onnx");
                    if (File.Exists(mo) && File.Exists(xu))
                        AvailableVersions.Add(new PairVersionOption(Path.GetFileName(dir), mo, xu));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "掃描雙 head 版本資料夾失敗: {Root}", PairsRoot);
        }

        if (AvailableVersions.Count == 0)
            StatusMessage = $"找不到任何版本。請把模型放到 {PairsRoot}\\<版本>\\{{mohao,xuehao}}.onnx";

        SelectedVersion =
            AvailableVersions.FirstOrDefault(v => v.Name == previousName)
            ?? AvailableVersions.FirstOrDefault(v => v.Name == CurrentVersionName)
            ?? AvailableVersions.FirstOrDefault();
    }

    /// <summary>把選定版本載入成目前使用的雙 head 模型。</summary>
    [RelayCommand]
    private void LoadModel()
    {
        var v = SelectedVersion;
        if (v is null || v.MohaoPath is null || v.XuehaoPath is null)
        {
            StatusMessage = "請先選擇一個版本。";
            return;
        }

        try
        {
            IsRunning = true;
            StatusMessage = $"載入中：{v.Name} …";
            _modelSwitch.LoadVersion(v.MohaoPath, v.XuehaoPath, v.Name);
            CurrentVersionName = _modelSwitch.CurrentVersionName ?? v.Name;
            StatusMessage = $"✓ 已載入並設為目前模型：{CurrentVersionName}。下一步：選測試影像資料夾→執行批量辨識。";
            _logger?.LogInformation("[PairMgmt] 已載入雙 head 模型版本: {Version}", v.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 載入失敗：{ex.Message}";
            _logger?.LogError(ex, "[PairMgmt] 載入雙 head 模型版本失敗: {Version}", v.Name);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>下一步：直接前往「批量推論」頁（沿用目前載入的版本，做工單核對 + 寫歷史），不必回選單。</summary>
    [RelayCommand]
    private void GoBatchInference() =>
        _navigation.ShowWindow<AIVision.Presentation.Wpf.Views.BatchInferenceView>();

    // ===== 下半：離線測試 =====

    [ObservableProperty]
    private string? selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    private bool isRunning;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool useSubfolderAsGroundTruth = true;

    /// <summary>
    /// 是否套用 appsettings 的相機 ROI（RoiX/Y/W/H）。離線資料集多為「已裁好的單顆鏡片」，
    /// 不該再套全幅相機 ROI（會裁錯 → Hough 找錯圓 → 誤判）。預設關閉，對齊 Python engine 的 apply_roi=False。
    /// 只有測試「相機全幅原始圖」時才需開啟。
    /// </summary>
    [ObservableProperty]
    private bool applyCameraRoi;

    public bool NotRunning => !IsRunning;

    public ObservableCollection<MoldCodePairBatchRow> Results { get; }

    // ===== 逐張播放（辨識過程）=====

    /// <summary>目前正在辨識的影像（原圖 + Hough 圓 + ROI 標註）。</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? currentPreview;

    /// <summary>目前影像的辨識結果文字（模號 / 穴號 / 信心）。</summary>
    [ObservableProperty]
    private string currentResultText = string.Empty;

    /// <summary>是否逐張播放辨識過程（關閉 = 不停留、快速跑完只填表）。</summary>
    [ObservableProperty]
    private bool playbackEnabled = true;

    /// <summary>每張之間停留毫秒（播放速度）。</summary>
    [ObservableProperty]
    private int playbackDelayMs = 350;

    private CancellationTokenSource? _cts;

    /// <summary>停止逐張播放。</summary>
    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var folder = await _folderPicker.PickFolderAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SelectedFolder = folder;
            _state.LastImageFolder = folder;   // 記住，供批量推論頁自動帶入
        }
    }

    [RelayCommand]
    private async Task RunBatchAsync()
    {
        bool useRemote = UseRemoteSource;

        // 走中央伺服器＝隔離試模：不需要本地模型；只有本機來源才要求先載入版本。
        if (!useRemote && !HasModelLoaded)
        {
            StatusMessage = "請先在上方選版本並「載入為目前模型」。（或改選來源=中央伺服器，不需本地模型）";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
        {
            StatusMessage = "請先選擇有效的測試影像資料夾。";
            return;
        }
        if (IsRunning) return;

        IsRunning = true;
        Results.Clear();
        CurrentPreview = null;
        CurrentResultText = string.Empty;

        var folder = SelectedFolder!;
        bool useTruth = UseSubfolderAsGroundTruth;
        var versionTag = CurrentVersionName;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 決定辨識來源。
        // 中央伺服器：先健檢（連不上/沒模型就不開跑），版本顯示 server 回報的 modelVersion。
        // 本機：預設「不套相機 ROI」（離線圖已裁）→ 用預設前處理(RoiW=0)重建當前版本辨識器，
        //       與 harness / 訓練端一致；勾選「套用相機 ROI」→ 用注入的目前模型（appsettings 前處理，含相機 ROI）。
        WarpPolarTwoHeadRecognizer? freshRec = null;
        IMoldCodePairRecognizerPort? recognizer = null;
        string sourceTag;
        if (useRemote)
        {
            StatusMessage = $"檢查中央伺服器… {ServerBaseUrl}";
            var health = await _remoteRecognizer.CheckHealthAsync(token);
            if (health is null)
            {
                StatusMessage = $"❌ 連不上中央伺服器：{ServerBaseUrl}。批量未開始（可先按「測試中央推論」排查）。";
                IsRunning = false;
                return;
            }
            if (!health.ModelLoaded)
            {
                StatusMessage = "⚠ 中央伺服器活著但未載入雙 head 模型 → 無法批量。請先在 server 配置模型。";
                IsRunning = false;
                return;
            }
            var requested = string.IsNullOrWhiteSpace(ServerModelVersion) ? null : ServerModelVersion!.Trim();
            sourceTag = requested is null
                ? $"中央伺服器({health.ModelVersion})"
                : $"中央伺服器(指定 {requested})";
            if (requested is not null)
                StatusMessage = $"以指定版本 {requested} 隔離試模（server 首張需冷載模型，會多 ~1 秒）…";
        }
        else if (ApplyCameraRoi)
        {
            recognizer = _recognizer;
            sourceTag = $"本機 {versionTag}";
        }
        else
        {
            var loaded = AvailableVersions.FirstOrDefault(
                v => v.Name == versionTag && v.MohaoPath is not null && v.XuehaoPath is not null);
            if (loaded is null)
            {
                StatusMessage = "找不到目前載入版本的模型檔，請重新載入版本。";
                IsRunning = false;
                return;
            }
            StatusMessage = "初始化辨識器（無相機 ROI）…";
            freshRec = await Task.Run(() =>
                new WarpPolarTwoHeadRecognizer(loaded.MohaoPath!, loaded.XuehaoPath!, new WarpPolarParams(), passes: 2));
            recognizer = freshRec;
            sourceTag = $"本機 {versionTag}";
        }

        // 模號正解 = 所選資料夾名稱；子資料夾名 = 穴號正解（對齊資料集 M101/01..18 結構）。
        string? mohaoTruth = useTruth ? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) : null;
        var items = BuildGroups(folder, useTruth);

        var times = new List<double>();
        var serverTimes = new List<double>();
        int idx = 0, total = 0, mohaoCorrect = 0, xuehaoCorrect = 0, bothCorrect = 0;
        int consecutiveTransportFails = 0;

        try
        {
            foreach (var (xuehaoTruth, file) in items)
            {
                token.ThrowIfCancellationRequested();
                idx++;
                StatusMessage = $"以 {sourceTag} 辨識中…（{idx}/{items.Count}）";

                // 重活（辨識 + 產生圓圖標註）在背景執行緒。
                PairObservation obs;
                byte[]? annotated;
                double ms;
                if (useRemote)
                {
                    var r = await RecognizeOneRemoteAsync(file, token,
                        string.IsNullOrWhiteSpace(ServerModelVersion) ? null : ServerModelVersion!.Trim());
                    obs = r.obs; annotated = r.annotated; ms = r.wallMs;

                    // 只有「傳輸層失敗」（連不上/逾時/5xx）算 server 故障；
                    // 「無鏡片/讀不到」是有效觀測，照常記錄。連續 3 次傳輸失敗 → 中止，避免整批慢磨。
                    if (r.transportFailed)
                    {
                        consecutiveTransportFails++;
                        if (consecutiveTransportFails >= 3)
                            throw new InvalidOperationException(
                                $"中央伺服器連續 {consecutiveTransportFails} 次傳輸失敗（{obs.FailureReason}），已中止批量。");
                    }
                    else
                    {
                        consecutiveTransportFails = 0;
                        if (r.serverMs > 0) serverTimes.Add(r.serverMs);
                    }
                }
                else
                {
                    (obs, annotated, ms) = await Task.Run(() => RecognizeOne(file, recognizer!), token);
                }
                times.Add(ms);

                string readMohao = !string.IsNullOrWhiteSpace(obs.Mohao) ? obs.Mohao! : "(none)";
                string readXuehao = !string.IsNullOrWhiteSpace(obs.Xuehao) ? obs.Xuehao! : "(none)";

                // ① 先呈現辨識過程：圓圖 + 結果。
                CurrentPreview = ToImage(annotated);
                CurrentResultText = obs.HasReading
                    ? $"模號 {readMohao}  ({obs.ConfMohao:F2})        穴號 {readXuehao}  ({obs.ConfXuehao:F2})"
                    : "無鏡片 / 讀取失敗（fail-closed）";

                // ② 計分 + 加入結果表。
                bool? mohaoMatch = null, xuehaoMatch = null, bothMatch = null;
                if (!string.IsNullOrEmpty(xuehaoTruth))
                {
                    total++;
                    bool mOk = !string.IsNullOrWhiteSpace(obs.Mohao) && !string.IsNullOrWhiteSpace(mohaoTruth) &&
                               Norm(obs.Mohao!) == Norm(mohaoTruth!);
                    bool xOk = !string.IsNullOrWhiteSpace(obs.Xuehao) &&
                               Norm(obs.Xuehao!) == Norm(xuehaoTruth!);
                    if (mOk) mohaoCorrect++;
                    if (xOk) xuehaoCorrect++;
                    if (mOk && xOk) bothCorrect++;
                    mohaoMatch = mOk; xuehaoMatch = xOk; bothMatch = mOk && xOk;
                }

                Results.Add(new MoldCodePairBatchRow(
                    Path.GetFileName(file),
                    mohaoTruth ?? "-", readMohao, obs.ConfMohao, mohaoMatch,
                    xuehaoTruth ?? "-", readXuehao, obs.ConfXuehao, xuehaoMatch,
                    bothMatch, file));

                // ③ 自動下一張：停留一下讓使用者看清楚。
                if (PlaybackEnabled && PlaybackDelayMs > 0)
                    await Task.Delay(PlaybackDelayMs, token);
            }

            // 準確率報告：來源 + 命中率 + 延遲。中央來源額外報 server 端純推論時間（供對照網路開銷）。
            string vtag = $"來源={sourceTag}　";
            string timing = useRemote
                ? $"單張來回 p50={Percentile(times, 0.5):F0}ms p95={Percentile(times, 0.95):F0}ms" +
                  $"（server 推論 p50={Percentile(serverTimes, 0.5):F0}ms p95={Percentile(serverTimes, 0.95):F0}ms）"
                : $"單張 p50={Percentile(times, 0.5):F1}ms p95={Percentile(times, 0.95):F1}ms (CPU)";
            StatusMessage = total > 0
                ? vtag + $"張數={Results.Count}　比對={total}　" +
                  $"模號正確={mohaoCorrect} ({Rate(mohaoCorrect, total)})　" +
                  $"穴號正確={xuehaoCorrect} ({Rate(xuehaoCorrect, total)})　" +
                  $"雙軸皆對={bothCorrect} ({Rate(bothCorrect, total)})　|　" + timing
                : vtag + $"張數={Results.Count}（無子資料夾正解，僅辨識）　|　" + timing;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"已停止（已辨識 {Results.Count} 張）。";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "模號穴號雙軸批量辨識失敗");
            StatusMessage = $"失敗：{ex.Message}";
        }
        finally
        {
            freshRec?.Dispose();
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>雙擊某列 → 開啟「辨識過程視覺化」窗（原圖標註 → 字帶 → 模型輸入 + 結果）。</summary>
    [RelayCommand]
    private void ShowProcess(MoldCodePairBatchRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.FullPath) || !File.Exists(row.FullPath))
            return;

        try
        {
            var img = MoldCodeImageLoader.LoadFromFile(row.FullPath);
            var trace = WarpPolarVisualizer.Explain(img, new WarpPolarParams());

            string result = trace.HoughFound
                ? $"模號 {row.ReadMohao} ({row.ConfMohao:F3})    穴號 {row.ReadXuehao} ({row.ConfXuehao:F3})"
                : "未偵測到鏡片（Hough miss）→ fail-closed";

            var vm = new RecognitionProcessViewModel(
                row.File, result, trace.HoughFound,
                ToImage(trace.OriginalAnnotated),
                ToImage(trace.PolarStrip),
                ToImage(trace.ModelInput));

            new AIVision.Presentation.Wpf.Views.RecognitionProcessView(vm)
            { Owner = System.Windows.Application.Current?.MainWindow }.Show();
        }
        catch (Exception ex)
        {
            StatusMessage = $"開啟辨識過程失敗：{ex.Message}";
            _logger?.LogError(ex, "開啟辨識過程視覺化失敗: {File}", row.FullPath);
        }
    }

    private static System.Windows.Media.ImageSource? ToImage(byte[]? png)
    {
        if (png is null || png.Length == 0)
            return null;
        var img = new System.Windows.Media.Imaging.BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>展開 (穴號正解, 影像檔) 清單。有子資料夾且啟用正解 → 每個子資料夾名為穴號正解；否則整夾平鋪。</summary>
    private List<(string? xuehaoTruth, string file)> BuildGroups(string folder, bool useTruth)
    {
        var list = new List<(string?, string)>();
        var dirs = Directory.GetDirectories(folder);
        if (useTruth && dirs.Length > 0)
        {
            foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
                foreach (var f in EnumerateImages(dir))
                    list.Add((Path.GetFileName(dir), f));
        }
        else
        {
            foreach (var f in EnumerateImages(folder))
                list.Add((null, f));
        }
        return list;
    }

    /// <summary>辨識一張 + 產生圓圖標註（背景執行緒）。回 (觀測, 標註PNG, 辨識耗時ms)。</summary>
    private static (PairObservation obs, byte[]? annotated, double ms) RecognizeOne(
        string file, IMoldCodePairRecognizerPort recognizer)
    {
        try
        {
            var img = MoldCodeImageLoader.LoadFromFile(file);
            var sw = Stopwatch.StartNew();
            var obs = recognizer.Recognize(img);
            sw.Stop();
            var trace = WarpPolarVisualizer.Explain(img, new WarpPolarParams());
            return (obs, trace.OriginalAnnotated, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return (PairObservation.Failed(ex.Message), null, 0);
        }
    }

    /// <summary>
    /// 走中央伺服器辨識一張（隔離試模）。圓圖標註仍在本機產生（與 server 同組前處理參數、僅供目視），
    /// 讀值/信心一律以 server 回應為準。回傳含 server 端純推論耗時與「傳輸層是否失敗」。
    /// </summary>
    private async Task<(PairObservation obs, byte[]? annotated, double wallMs, int serverMs, bool transportFailed)>
        RecognizeOneRemoteAsync(string file, CancellationToken ct, string? modelVersion = null)
    {
        try
        {
            var img = await Task.Run(() => MoldCodeImageLoader.LoadFromFile(file), ct);
            var sw = Stopwatch.StartNew();
            var obs = await _remoteRecognizer.RecognizeAsync(img, ct, modelVersion);
            sw.Stop();
            var annotated = await Task.Run(
                () => WarpPolarVisualizer.Explain(img, new WarpPolarParams()).OriginalAnnotated, ct);
            return (obs, annotated, sw.Elapsed.TotalMilliseconds,
                    _remoteRecognizer.LastServerElapsedMs, _remoteRecognizer.LastCallFailed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (PairObservation.Failed(ex.Message), null, 0, 0, true);
        }
    }

    private static string Norm(string s) => s.Trim().ToUpperInvariant();

    private static string Rate(int correct, int total) =>
        total > 0 ? ((double)correct / total).ToString("P2") : "—";

    private static IEnumerable<string> EnumerateImages(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
    }
}

/// <summary>可選模型版本（一組 mohao + xuehao .onnx）。</summary>
public sealed record PairVersionOption(string Name, string? MohaoPath, string? XuehaoPath)
{
    public string? Folder => MohaoPath is null ? null : Path.GetDirectoryName(MohaoPath);
    public override string ToString() => Name;
}

/// <summary>雙軸批量辨識單列結果（供 DataGrid 綁定）。<see cref="FullPath"/> 供雙擊開啟辨識過程，不顯示。</summary>
public sealed record MoldCodePairBatchRow(
    string File,
    string ExpectedMohao, string ReadMohao, double ConfMohao, bool? MohaoMatch,
    string ExpectedXuehao, string ReadXuehao, double ConfXuehao, bool? XuehaoMatch,
    bool? BothMatch,
    string FullPath);
