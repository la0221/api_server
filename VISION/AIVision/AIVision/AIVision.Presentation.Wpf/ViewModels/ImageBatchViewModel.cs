using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIVision.Application.Contracts.ImageBatch;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;
using AIVision.Presentation.Wpf.Utilities;
using AIVision.Presentation.Wpf.Adapters.ImageBatch;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AIiInferencePort = AIVision.Application.Ports.ImageBatch.IAiInferencePort;
using AIVision.Infrastructure.Configs;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class ImageBatchViewModel : ObservableObject, IAsyncDisposable
{
    private const string UncategorizedLabel = "Uncategorized";
    private const string ErrorLabel = "Error";

    private readonly IFolderPickerPort _folderPicker;
    private readonly IImageEnumeratorPort _enumerator;
    private readonly IImageLoaderPort _loader;
    private readonly AIiInferencePort _ai;
    private readonly IOverlayRendererPort _overlay;
    private readonly IImageWriterPort _writer;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;
    public bool OverlaySupported { get; }

    private readonly Dictionary<string, DefectStatViewModel> _statsLookup = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private readonly object _pauseLock = new();
    private TaskCompletionSource<bool>? _resumeTcs;
    private bool _pauseRequested;

    public ImageBatchViewModel(
        IFolderPickerPort folderPicker,
        IImageEnumeratorPort enumerator,
        IImageLoaderPort loader,
        AIiInferencePort ai,
        IOverlayRendererPort overlay,
        IImageWriterPort writer,
        IMessenger messenger,
        IOptions<AiSettings> aiOptions)
    {
        _folderPicker = folderPicker;
        _enumerator = enumerator;
        _loader = loader;
        _ai = ai;
        _overlay = overlay;
        _writer = writer;
        _messenger = messenger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        OverlaySupported = string.Equals(aiOptions.Value.Mode, "segmentation", StringComparison.OrdinalIgnoreCase);
        if (!OverlaySupported)
        {
            OverlayEnabled = false;
        }

        StatusMessage = "尚未開始";
    }

    public ObservableCollection<DefectStatViewModel> Stats { get; } = new();

    [ObservableProperty]
    private string rootPath = string.Empty;

    [ObservableProperty]
    private bool saveEnabled;

    [ObservableProperty]
    private bool overlayEnabled;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private int processedCount;

    [ObservableProperty]
    private int pendingCount;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private BitmapSource? previewBitmap;

    [ObservableProperty]
    private string currentLabel = string.Empty;

    [ObservableProperty]
    private double currentConfidence;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _folderPicker.PickFolderAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(path))
        {
            RootPath = path;
            StatusMessage = $"已選擇：{path}";
        }
    }

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (!IsRunning)
        {
            await StartAsync();
        }
        else if (IsPaused)
        {
            Resume();
        }
    }

    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning || IsPaused)
        {
            return;
        }

        lock (_pauseLock)
        {
            _pauseRequested = true;
            _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        IsPaused = true;
        StatusMessage = "已暫停";
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
        if (_processingTask is not null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        ResetState("已停止");
    }

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
        {
            StatusMessage = "請選擇有效的影像資料夾路徑。";
            return;
        }

        if (_processingTask is { IsCompleted: false })
        {
            return;
        }

        var files = await LoadFileListAsync(CancellationToken.None);
        if (files.Count == 0)
        {
            StatusMessage = "資料夾中沒有可處理的影像。";
            return;
        }

        InitializeStats(files);

        ResetCounters(files.Count);

        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsPaused = false;
        StatusMessage = "開始處理...";
        PreviewBitmap = null;

        _processingTask = ProcessFilesAsync(files, _cts.Token);

        try
        {
            await _processingTask;
            if (_cts.IsCancellationRequested)
            {
                ResetState("已停止");
            }
            else
            {
                StatusMessage = $"完成，總共處理 {ProcessedCount} 張影像。";
                IsRunning = false;
            }
        }
        catch (OperationCanceledException)
        {
            ResetState("已停止");
        }
        catch (Exception ex)
        {
            ResetState($"發生錯誤：{ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _processingTask = null;
        }
    }

    private void ResetCounters(int total)
    {
        TotalCount = total;
        ProcessedCount = 0;
        PendingCount = total;
        ProgressPercent = 0;
    }

    private void ResetState(string message)
    {
        IsRunning = false;
        IsPaused = false;
        StatusMessage = message;

        lock (_pauseLock)
        {
            _pauseRequested = false;
            _resumeTcs?.TrySetResult(true);
            _resumeTcs = null;
        }
    }

    private async Task<List<string>> LoadFileListAsync(CancellationToken ct)
    {
        var list = new List<string>();
        await foreach (var file in _enumerator.EnumerateAsync(RootPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(file);
        }

        return list;
    }

    private void InitializeStats(IEnumerable<string> files)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(RootPath, file);
            var folder = Path.GetDirectoryName(relative);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var topFolder = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(topFolder))
                {
                    labels.Add(topFolder);
                }
            }
        }

        _dispatcher.Invoke(() =>
        {
            Stats.Clear();
            _statsLookup.Clear();

            foreach (var label in labels.OrderBy(l => l))
            {
                AddStat(label);
            }

            AddStat(UncategorizedLabel);
            AddStat(ErrorLabel);
        });
    }

    private void AddStat(string label)
    {
        if (_statsLookup.ContainsKey(label))
        {
            return;
        }

        var vm = new DefectStatViewModel(label);
        _statsLookup[label] = vm;
        Stats.Add(vm);
    }

    private async Task ProcessFilesAsync(List<string> files, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct);

            try
            {
                var image = await _loader.LoadAsync(file, ct);
                System.Diagnostics.Debug.WriteLine($"[ImageBatch] 載入圖片：{Path.GetFileName(file)}");
                
                // 【離線模式】嘗試從 JSON 檔案讀取推論結果
                var jsonPath = Path.ChangeExtension(file, ".json");
                AiResult? result = null;
                
                if (File.Exists(jsonPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBatch] 找到 JSON：{Path.GetFileName(jsonPath)}");
                    // 從 JSON 讀取推論結果（不執行推論）
                    result = await LoadResultFromJsonAsync(jsonPath, ct);
                    StatusMessage = $"從 JSON 載入結果：{Path.GetFileName(file)}";
                    
                    if (result != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] JSON 解析成功：PredictedClass={result.PredictedClass}, MaskCount={result.SegmentationMasks?.Count ?? 0}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] JSON 解析失敗，result 為 null");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBatch] 未找到 JSON，執行在線推論");
                    // 【在線模式】執行 AI 推論（如果沒有 JSON）
                    result = await _ai.PredictAsync(image, ct);
                    StatusMessage = $"執行推論：{Path.GetFileName(file)}";
                }
                
                if (result == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBatch] result 為 null，跳過此檔案");
                    await UpdateStatsAsync(UncategorizedLabel);
                    continue;
                }

                // 【分割模式】優先處理：即使同時帶有 PredictedClass，也先疊圖
                if (result.SegmentationMasks is { Count: > 0 })
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBatch] 進入分割模式：MaskCount={result.SegmentationMasks.Count}");
                    System.Diagnostics.Debug.WriteLine($"[ImageBatch] OverlayEnabled={OverlayEnabled}, _overlay類型={_overlay?.GetType().Name}");
                    
                    BitmapSource? previewBitmap = null;

                    // 如果啟用 Overlay，繪製遮罩
                    if (OverlaySupported && OverlayEnabled && _overlay is SegOverlayRenderer segRenderer)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] 準備繪製 overlay...");
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"繪製 overlay：{Path.GetFileName(file)} (共 {result.SegmentationMasks.Count} 個 mask)";
                        });
                        
                        previewBitmap = segRenderer.DrawAiMasks(BitmapSourceFactory.FromImageData(image), result);
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] overlay 繪製完成");
                        
                        // 更新 UI 預覽（使用疊加後的圖）
                        await _dispatcher.InvokeAsync(() =>
                        {
                            PreviewBitmap = previewBitmap;
                            _messenger.Send(new BatchPreviewMessage(image, file));
                        });
                    }
                    else
                    {
                        // 不使用 Overlay，顯示原圖
                        var reason = !OverlaySupported
                            ? "模型模式不支援 Overlay"
                            : (!OverlayEnabled ? "未勾選 Overlay" : "_overlay 類型不符");
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] 跳過 overlay：{reason}");
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"跳過 overlay：{Path.GetFileName(file)} ({reason})";
                        });
                        await UpdatePreviewAsync(image, file);
                    }

                    // 按各缺陷類別統計
                    foreach (var maskClass in result.SegmentationMasks.Select(m => m.ClassName).Distinct())
                    {
                        await UpdateStatsAsync(maskClass);
                    }

                    // 存圖邏輯
                    if (SaveEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] 準備存檔：SaveEnabled=true");
                        var label = string.Join("_", result.SegmentationMasks.Select(m => m.ClassName).Distinct());
                        var destination = Path.Combine(RootPath, "_output",
                            string.IsNullOrWhiteSpace(label) ? "defects" : label);
                        Directory.CreateDirectory(destination);
                        var outPath = Path.Combine(destination, Path.GetFileName(file));
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] 存檔路徑：{outPath}");

                        // 如果有 overlay bitmap，保存為疊加後的圖；否則保存原圖
                        if (OverlaySupported && OverlayEnabled && previewBitmap != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ImageBatch] 存檔模式：含 overlay (previewBitmap 不為 null)");
                            // 將 BitmapSource 保存為檔案
                            await _dispatcher.InvokeAsync(() =>
                            {
                                StatusMessage = $"存檔 (含 overlay)：{Path.GetFileName(outPath)}";
                                SaveBitmapSourceToFile(previewBitmap, outPath);
                            });
                            System.Diagnostics.Debug.WriteLine($"[ImageBatch] 存檔完成 (含 overlay)");
                        }
                        else
                        {
                            var saveReason = !OverlaySupported
                                ? "模型模式不支援 Overlay"
                                : (!OverlayEnabled ? "Overlay 未勾選" : "無 preview bitmap");
                            System.Diagnostics.Debug.WriteLine($"[ImageBatch] 存檔模式：原圖 ({saveReason})");
                            await _dispatcher.InvokeAsync(() =>
                            {
                                StatusMessage = $"存檔 (原圖)：{Path.GetFileName(outPath)} ({saveReason})";
                            });
                            // 存原圖直接複製原始檔(不 decode→重編碼),避免二次 JPEG 壓縮降質;與模號批量頁一致。
                            await CopyOriginalAsync(file, outPath, ct);
                            System.Diagnostics.Debug.WriteLine($"[ImageBatch] 存檔完成 (原圖)");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] 跳過存檔：SaveEnabled=false");
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"跳過存檔：未勾選 Save 選項";
                        });
                    }

                    continue; // 處理下一張圖片
                }

                // 【分類/檢測模式】只返回類別和信心分數
                if (!string.IsNullOrEmpty(result.PredictedClass))
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBatch] 進入分類模式：PredictedClass={result.PredictedClass}");
                    CurrentLabel = result.PredictedClass;
                    CurrentConfidence = result.Confidence ?? 0d;

                    await UpdateStatsAsync(result.PredictedClass);
                    await UpdatePreviewAsync(image, file);

                    if (SaveEnabled)
                    {
                        var destination = Path.Combine(RootPath, "_output", result.PredictedClass);
                        Directory.CreateDirectory(destination);
                        var outPath = Path.Combine(destination, Path.GetFileName(file));
                        // 分類模式存原圖:直接複製原始檔(不重編碼),避免二次壓縮降質。
                        await CopyOriginalAsync(file, outPath, ct);
                        System.Diagnostics.Debug.WriteLine($"[ImageBatch] 分類模式存檔完成：{outPath}");
                    }

                    continue; // 處理下一張圖片
                }

                // 無推論結果
                await UpdateStatsAsync(UncategorizedLabel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await UpdateStatsAsync(ErrorLabel);
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"處理 {Path.GetFileName(file)} 時發生錯誤：{ex.Message}";
                });
            }
        }

        stopwatch.Stop();
        await _dispatcher.InvokeAsync(() =>
        {
            ProgressPercent = 100;
            StatusMessage = $"完成，耗時 {stopwatch.Elapsed:hh\\:mm\\:ss}";
        });
    }

    /// <summary>
    /// 存「原圖」時直接複製來源檔(不 decode→重編碼),避免二次壓縮抹平淺刻低對比訊號;
    /// 來源與目的同一路徑時跳過(原圖已就地,避免 File.Copy 同路徑丟例外)。
    /// </summary>
    private static async Task CopyOriginalAsync(string sourceFile, string destPath, CancellationToken ct)
    {
        var sameFile = string.Equals(
            Path.GetFullPath(sourceFile), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase);
        if (!sameFile)
            await Task.Run(() => File.Copy(sourceFile, destPath, overwrite: true), ct);
    }

    private async Task UpdatePreviewAsync(ImageData data, string path)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            PreviewBitmap = BitmapSourceFactory.FromImageData(data);
            _messenger.Send(new BatchPreviewMessage(data, path));
        });
    }

    private async Task UpdateStatsAsync(string label)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (!_statsLookup.TryGetValue(label, out var stat))
            {
                AddStat(label);
                stat = _statsLookup[label];
            }

            stat.Count += 1;
            ProcessedCount += 1;
            PendingCount = Math.Max(TotalCount - ProcessedCount, 0);
            ProgressPercent = TotalCount == 0 ? 0 : (double)ProcessedCount / TotalCount * 100d;

            var denominator = Math.Max(ProcessedCount, 1);
            foreach (var entry in Stats)
            {
                entry.Ratio = denominator == 0 ? 0 : (double)entry.Count / denominator;
            }
        });
    }

    private async Task WaitIfPausedAsync(CancellationToken token)
    {
        Task? waitTask = null;
        lock (_pauseLock)
        {
            if (_pauseRequested && _resumeTcs != null)
            {
                waitTask = _resumeTcs.Task;
            }
        }

        if (waitTask is not null)
        {
            await waitTask.WaitAsync(token);
        }
    }

    private void Resume()
    {
        TaskCompletionSource<bool>? resumeSignal = null;
        lock (_pauseLock)
        {
            if (!_pauseRequested)
            {
                return;
            }

            _pauseRequested = false;
            resumeSignal = _resumeTcs;
            _resumeTcs = null;
        }

        resumeSignal?.TrySetResult(true);
        IsPaused = false;
        StatusMessage = "繼續處理...";
    }

    private void SaveBitmapSourceToFile(BitmapSource bitmap, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        BitmapEncoder encoder;

        // 根據副檔名選擇編碼器
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        encoder = ext switch
        {
            ".png" => new System.Windows.Media.Imaging.PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new System.Windows.Media.Imaging.BmpBitmapEncoder(),
            _ => new System.Windows.Media.Imaging.PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    /// <summary>
    /// 從批量推論生成的 JSON 檔案中載入推論結果
    /// </summary>
    private async Task<AiResult?> LoadResultFromJsonAsync(string jsonPath, CancellationToken ct)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[LoadJson] 讀取 JSON：{Path.GetFileName(jsonPath)}");
            var jsonContent = await File.ReadAllTextAsync(jsonPath, ct);
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            // 解析批量推論的 JSON 格式
            if (!root.TryGetProperty("predicted_class", out var classEl))
            {
                System.Diagnostics.Debug.WriteLine($"[LoadJson] JSON 中沒有 'predicted_class' 欄位");
                return null;
            }

            var predictedClass = classEl.GetString();
            var confidence = root.TryGetProperty("confidence", out var confEl) 
                ? confEl.GetDouble() 
                : 1.0;
            System.Diagnostics.Debug.WriteLine($"[LoadJson] predicted_class={predictedClass}, confidence={confidence}");

            // 解析 raw_response 中的 mask 數據
            List<SegMask>? masks = null;
            if (root.TryGetProperty("raw_response", out var rawResp) &&
                rawResp.TryGetProperty("data", out var data) &&
                data.TryGetProperty("predictions", out var predictions))
            {
                System.Diagnostics.Debug.WriteLine($"[LoadJson] 找到 raw_response.data.predictions");
                
                // 獲取第一個 prediction（只有一個檔案）
                var firstPrediction = predictions.EnumerateObject().FirstOrDefault().Value;
                
                if (firstPrediction.TryGetProperty("base64Image", out var base64Array) &&
                    base64Array.ValueKind == JsonValueKind.Array)
                {
                    System.Diagnostics.Debug.WriteLine($"[LoadJson] 找到 base64Image 陣列，長度={base64Array.GetArrayLength()}");
                    masks = new List<SegMask>();
                    
                    foreach (var maskItem in base64Array.EnumerateArray())
                    {
                        if (maskItem.TryGetProperty("class", out var maskClass) &&
                            maskItem.TryGetProperty("mask", out var maskData))
                        {
                            var className = maskClass.GetString() ?? "unknown";
                            var maskBase64 = maskData.GetString() ?? "";
                            System.Diagnostics.Debug.WriteLine($"[LoadJson] 解析 mask：class={className}, base64長度={maskBase64.Length}");
                            
                            try
                            {
                                var maskBytes = Convert.FromBase64String(maskBase64);
                                System.Diagnostics.Debug.WriteLine($"[LoadJson] Base64 解碼成功，byte長度={maskBytes.Length}");
                                masks.Add(new SegMask
                                {
                                    ClassName = className,
                                    PngBytes = maskBytes
                                });
                            }
                            catch (FormatException ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[LoadJson] Base64 解碼失敗：{ex.Message}");
                                // 跳過無效的 mask
                            }
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[LoadJson] 成功解析 {masks.Count} 個 mask");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[LoadJson] 未找到 base64Image 或格式不正確");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[LoadJson] 未找到 raw_response.data.predictions 路徑");
            }

            var result = new AiResult
            {
                PredictedClass = predictedClass,
                Confidence = confidence,
                SegmentationMasks = masks
            };
            System.Diagnostics.Debug.WriteLine($"[LoadJson] 建立 AiResult：PredictedClass={result.PredictedClass}, MaskCount={result.SegmentationMasks?.Count ?? 0}");
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadJson] JSON 解析失敗：{ex.Message}\n{ex.StackTrace}");
            // JSON 解析失敗，返回 null
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
