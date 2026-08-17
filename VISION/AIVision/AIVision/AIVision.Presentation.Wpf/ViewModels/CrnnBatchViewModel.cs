using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Infrastructure.MoldCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 「CRNN 測試（中央推論）」頁——引擎並行期（2026-08-04 拍板：CRNN 逐步取代雙 head）的 CRNN 專屬測試 UI。
/// <para>
/// CRNN **只在 server 跑**（python sidecar），edge 沒有本地版——所以本頁一律走中央；
/// 選測試資料夾 → 整批送 <c>POST /api/infer/ocr_crnn</c> → 準確率/複檢率/延遲報告。
/// 資料夾正解慣例與雙 head 頁相同：資料夾名=模號正解、子資料夾名=穴號正解。
/// </para>
/// <para>⚠ CRNN 無 NG 類：品質旗標=needsReview（信心低於門檻）；首張會觸發 sidecar 冷啟（可達 20-90 秒）。</para>
/// </summary>
public partial class CrnnBatchViewModel : ObservableObject
{
    private readonly CrnnInferClient _client;
    private readonly ModelHubClient _modelHub;
    private readonly IFolderPickerPort _folderPicker;
    private readonly Services.PairWorkflowState _state;
    private readonly InferenceServerOptions _serverOptions;
    private readonly ILogger<CrnnBatchViewModel>? _logger;

    public CrnnBatchViewModel(
        CrnnInferClient client,
        ModelHubClient modelHub,
        IFolderPickerPort folderPicker,
        Services.PairWorkflowState state,
        IOptions<InferenceServerOptions> serverOptions,
        IOptions<Models.TestImageFolderOptions>? testFolders = null,
        ILogger<CrnnBatchViewModel>? logger = null)
    {
        _client = client;
        _modelHub = modelHub;
        _folderPicker = folderPicker;
        _state = state;
        _serverOptions = serverOptions.Value;
        _logger = logger;
        ServerBaseUrl = _serverOptions.BaseUrl;
        TestFolderOptions = new ObservableCollection<string>(
            testFolders?.Value.Paths ?? new List<string>());
        SelectedFolder = state.LastImageFolder;
        Results = new ObservableCollection<CrnnBatchRow>();
        StatusMessage = "步驟①：健康檢查確認 CRNN 就緒。步驟②：選測試影像資料夾→執行批量。首張會冷啟 sidecar（20-90 秒），之後每張 ~0.1-0.2 秒。";
    }

    /// <summary>「測試資料夾」下拉常用路徑（appsettings TestImageFolders；可貼任意路徑）。</summary>
    public ObservableCollection<string> TestFolderOptions { get; }

    public ObservableCollection<CrnnBatchRow> Results { get; }

    [ObservableProperty]
    private string? serverBaseUrl;

    [ObservableProperty]
    private string? healthText;

    /// <summary>
    /// 指定 server 端 CRNN 版本（登錄庫 ocr_crnn；留空=server 預設版）。多版本熱切換（AINavi 借鏡①）：
    /// 新 CRNN 版本只需發布到 server，這裡指定即可整批試——免改設定免重啟。
    /// </summary>
    [ObservableProperty]
    private string? serverModelVersion;

    /// <summary>server 登錄庫 ocr_crnn 版本下拉（按「查伺服器版本」載入；可手填）。</summary>
    public ObservableCollection<string> ServerVersionOptions { get; } = new();

    /// <summary>向 server 要 ocr_crnn 版本清單，填入下拉。</summary>
    [RelayCommand]
    private async Task FetchServerVersionsAsync()
    {
        StatusMessage = $"查詢伺服器 CRNN 版本清單… {ServerBaseUrl}";
        var list = await _modelHub.ListAsync("ocr_crnn");
        if (list is null)
        {
            StatusMessage = $"❌ 拿不到版本清單：{ServerBaseUrl}（server 未啟動或不可達）。";
            return;
        }
        ServerVersionOptions.Clear();
        foreach (var v in list.Versions)
            if (!string.IsNullOrWhiteSpace(v.Version))
                ServerVersionOptions.Add(v.Version!);
        StatusMessage = $"伺服器 ocr_crnn 有 {ServerVersionOptions.Count} 個版本。選一個或留空=server 預設版。";
    }

    [ObservableProperty]
    private string? selectedFolder;

    [ObservableProperty]
    private bool useSubfolderAsGroundTruth = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    private bool isRunning;

    public bool NotRunning => !IsRunning;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>目前送出的影像預覽（CRNN 前處理在 server 端，此處僅原圖）。</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? currentPreview;

    [ObservableProperty]
    private string currentResultText = string.Empty;

    private CancellationTokenSource? _cts;

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>CRNN 健檢（不觸發冷啟）。</summary>
    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        HealthText = $"檢查中… {_serverOptions.BaseUrl}";
        var h = await _client.CheckHealthAsync();
        if (h is null)
        {
            HealthText = $"❌ 連不上 server：{_serverOptions.BaseUrl}（先確認 API server 已啟動——根目錄「啟動API伺服器.bat」）。";
            return;
        }
        var loaded = string.Join("、", h.LoadedVersions
            .Where(v => !string.IsNullOrWhiteSpace(v.Version))
            .Select(v => $"{v.Version}{(v.Ready ? "" : "(啟動中)")}"));
        HealthText = h.Status switch
        {
            "disabled" => "⚠ server 活著，但 CRNN sidecar 未啟用（server appsettings CrnnSidecar:Enabled）。",
            "cold" => $"✅ CRNN 已啟用（行程池空）｜預設版 {h.DefaultVersion}｜首張推論會冷啟該版（20-90 秒）。",
            "ready" => $"✅ CRNN 就緒｜預設版 {h.DefaultVersion}｜池中：{loaded}（熱請求 ~0.1-0.2 秒/張）",
            _ => $"狀態 {h.Status}",
        };
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var folder = await _folderPicker.PickFolderAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SelectedFolder = folder;
            _state.LastImageFolder = folder;
        }
    }

    [RelayCommand]
    private async Task RunBatchAsync()
    {
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
        string? mohaoTruth = useTruth ? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) : null;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var items = BuildGroups(folder, useTruth);
        var wallTimes = new List<double>();
        var sidecarTimes = new List<double>();
        int idx = 0, total = 0, mohaoCorrect = 0, xuehaoCorrect = 0, bothCorrect = 0, reviewCount = 0;
        int consecutiveTransportFails = 0;
        string? version = null;

        try
        {
            foreach (var (xuehaoTruth, file) in items)
            {
                token.ThrowIfCancellationRequested();
                idx++;
                StatusMessage = idx == 1
                    ? $"CRNN 辨識中…（1/{items.Count}；首張含 sidecar 冷啟，最多等 90 秒）"
                    : $"CRNN 辨識中…（{idx}/{items.Count}）";

                // 端點 v1 只收 PNG：非 png 檔在本地無損轉一次（WPF 內建編碼器）。
                var png = await Task.Run(() => LoadAsPng(file), token);
                CurrentPreview = ToImage(png);

                var sw = Stopwatch.StartNew();
                var dto = await _client.RecognizeAsync(png, "CRNN-TEST", token,
                    string.IsNullOrWhiteSpace(ServerModelVersion) ? null : ServerModelVersion!.Trim());
                sw.Stop();

                if (dto is null)   // 傳輸層失敗（連不上/逾時/503）才算故障
                {
                    consecutiveTransportFails++;
                    CurrentResultText = "傳輸失敗（server/sidecar 不可用）";
                    Results.Add(new CrnnBatchRow(
                        Path.GetFileName(file), mohaoTruth ?? "-", "(fail)", 0, null,
                        xuehaoTruth ?? "-", "(fail)", 0, null, null, false, file));
                    if (consecutiveTransportFails >= 3)
                        throw new InvalidOperationException("CRNN 連續 3 次傳輸失敗，已中止批量（按健康檢查排查）。");
                    continue;
                }
                consecutiveTransportFails = 0;
                version ??= dto.ModelVersion;
                wallTimes.Add(sw.Elapsed.TotalMilliseconds);
                if (dto.SidecarLatencyMs > 0) sidecarTimes.Add(dto.SidecarLatencyMs);
                if (dto.NeedsReview) reviewCount++;

                string readMohao = dto.HasReading ? dto.Mohao ?? "(none)" : "(none)";
                string readXuehao = dto.HasReading ? dto.Xuehao ?? "(none)" : "(none)";
                CurrentResultText = dto.HasReading
                    ? $"模號 {readMohao}  ({dto.ConfMohao:F2})        穴號 {readXuehao}  ({dto.ConfXuehao:F2})" +
                      (dto.NeedsReview ? "        ⚠ 建議複檢" : "")
                    : $"無鏡片 / 讀取失敗（{dto.FailureReason ?? "fail-closed"}）";

                bool? mOk = null, xOk = null, bOk = null;
                if (!string.IsNullOrEmpty(xuehaoTruth))
                {
                    total++;
                    bool m = dto.HasReading && !string.IsNullOrWhiteSpace(mohaoTruth) &&
                             Norm(readMohao) == Norm(mohaoTruth!);
                    bool x = dto.HasReading && Norm(readXuehao) == Norm(xuehaoTruth!);
                    if (m) mohaoCorrect++;
                    if (x) xuehaoCorrect++;
                    if (m && x) bothCorrect++;
                    mOk = m; xOk = x; bOk = m && x;
                }

                Results.Add(new CrnnBatchRow(
                    Path.GetFileName(file),
                    mohaoTruth ?? "-", readMohao, dto.ConfMohao, mOk,
                    xuehaoTruth ?? "-", readXuehao, dto.ConfXuehao, xOk,
                    bOk, dto.NeedsReview, file));
            }

            string timing =
                $"單張來回 p50={Percentile(wallTimes, 0.5):F0}ms p95={Percentile(wallTimes, 0.95):F0}ms" +
                $"（sidecar p50={Percentile(sidecarTimes, 0.5):F0}ms）";
            string vtag = $"引擎=CRNN({version ?? "?"})　";
            StatusMessage = total > 0
                ? vtag + $"張數={Results.Count}　比對={total}　" +
                  $"模號正確={mohaoCorrect} ({Rate(mohaoCorrect, total)})　" +
                  $"穴號正確={xuehaoCorrect} ({Rate(xuehaoCorrect, total)})　" +
                  $"雙軸皆對={bothCorrect} ({Rate(bothCorrect, total)})　" +
                  $"建議複檢={reviewCount}　|　" + timing
                : vtag + $"張數={Results.Count}（無子資料夾正解，僅辨識）　建議複檢={reviewCount}　|　" + timing;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"已停止（已辨識 {Results.Count} 張）。";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CRNN 批量失敗");
            StatusMessage = $"失敗：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>讀檔並保證是 PNG bytes（.png 原樣；其他格式用 WPF 編碼器無損轉）。</summary>
    private static byte[] LoadAsPng(string file)
    {
        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(file);

        using var src = File.OpenRead(file);
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
            src, System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(frame);
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static System.Windows.Media.ImageSource? ToImage(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        var img = new System.Windows.Media.Imaging.BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>展開 (穴號正解, 檔案) 清單——慣例同雙 head 頁：子資料夾名=穴號正解。</summary>
    private static List<(string? xuehaoTruth, string file)> BuildGroups(string folder, bool useTruth)
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

    private static IEnumerable<string> EnumerateImages(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string Norm(string s) => s.Trim().ToUpperInvariant();

    private static string Rate(int correct, int total) =>
        total > 0 ? ((double)correct / total).ToString("P2") : "—";

    private static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
    }
}

/// <summary>CRNN 批量單列結果（DataGrid 綁定）。</summary>
public sealed record CrnnBatchRow(
    string File,
    string ExpectedMohao, string ReadMohao, double ConfMohao, bool? MohaoMatch,
    string ExpectedXuehao, string ReadXuehao, double ConfXuehao, bool? XuehaoMatch,
    bool? BothMatch, bool NeedsReview,
    string FullPath);
