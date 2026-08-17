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
using AIVision.Application.Contracts.WorkOrder;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Services;
using AIVision.Domain.Entities;
using AIVision.Domain.Shared;
using ImageBatchIAiInferencePort = AIVision.Application.Ports.ImageBatch.IAiInferencePort;
using AIVision.Presentation.Wpf.Utilities;
using AIVision.Presentation.Wpf.Adapters.ImageBatch;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using AIVision.Infrastructure.Adapters.AiInference;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 批量推論 ViewModel
/// 功能：選擇資料夾 → 執行推論 → 儲存 JSON 和照片並分類
/// </summary>
public partial class BatchInferenceViewModel : ObservableObject, IAsyncDisposable
{
    private const string UncategorizedLabel = "Uncategorized";
    private const string ErrorLabel = "Error";

    private readonly IFolderPickerPort _folderPicker;
    private readonly IImageEnumeratorPort _enumerator;
    private readonly IImageLoaderPort _loader;
    private readonly ImageBatchIAiInferencePort _ai;
    private readonly IImageWriterPort _writer;
    private readonly IMessenger _messenger;
    private readonly IWorkOrderManagementService _workOrderService;
    private readonly IInspectionRepository _inspectionRepository;
    private readonly INavigationService _navigationService;
    private readonly ModelConfigService _modelConfigService;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<BatchInferenceViewModel>? _logger;

    private readonly Dictionary<string, DefectStatViewModel> _statsLookup = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private readonly object _pauseLock = new();
    private TaskCompletionSource<bool>? _resumeTcs;
    private bool _pauseRequested;
    private Guid? _currentWorkOrderId;

    public BatchInferenceViewModel(
        IFolderPickerPort folderPicker,
        IImageEnumeratorPort enumerator,
        IImageLoaderPort loader,
        ImageBatchIAiInferencePort ai,
        IImageWriterPort writer,
        IMessenger messenger,
        IWorkOrderManagementService workOrderService,
        IInspectionRepository inspectionRepository,
        INavigationService navigationService,
        ModelConfigService modelConfigService,
        ILogger<BatchInferenceViewModel>? logger = null)
    {
        _folderPicker = folderPicker;
        _enumerator = enumerator;
        _loader = loader;
        _ai = ai;
        _writer = writer;
        _messenger = messenger;
        _workOrderService = workOrderService;
        _inspectionRepository = inspectionRepository;
        _navigationService = navigationService;
        _modelConfigService = modelConfigService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _logger = logger;

        StatusMessage = "尚未開始";

        // 訂閱工單變更訊息
        _messenger.Register<WorkOrderChangedMessage>(this, async (_, _) =>
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                await LoadCurrentWorkOrderAsync();
            });
        });

        // 載入當前工單
        _ = LoadCurrentWorkOrderAsync();
    }

    public ObservableCollection<DefectStatViewModel> Stats { get; } = new();

    [ObservableProperty]
    private string rootPath = string.Empty;

    [ObservableProperty]
    private string outputPath = string.Empty;

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

    [ObservableProperty]
    private string currentWorkOrderCode = "無工單";

    [ObservableProperty]
    private string currentWorkOrderProduct = "未設定";

    private async Task LoadCurrentWorkOrderAsync()
    {
        try
        {
            var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (workOrder != null)
            {
                _currentWorkOrderId = workOrder.Id;
                CurrentWorkOrderCode = workOrder.Code;
                CurrentWorkOrderProduct = workOrder.ProductName;
            }
            else
            {
                _currentWorkOrderId = null;
                CurrentWorkOrderCode = "無工單";
                CurrentWorkOrderProduct = "未設定";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "載入當前工單失敗");
            _currentWorkOrderId = null;
            CurrentWorkOrderCode = "無工單";
            CurrentWorkOrderProduct = "未設定";
        }
    }

    [RelayCommand]
    private void SelectWorkOrder()
    {
        // 打開工單管理視窗選擇工單
        _navigationService.ShowDialog<Views.WorkOrderManagementView>();
        // 工單變更後會自動觸發 WorkOrderChangedMessage，無需手動重新載入
    }

    [RelayCommand]
    private async Task BrowseInputFolderAsync()
    {
        var path = await _folderPicker.PickFolderAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(path))
        {
            RootPath = path;
            StatusMessage = $"已選擇輸入資料夾：{path}";
        }
    }

    [RelayCommand]
    private async Task BrowseOutputFolderAsync()
    {
        var path = await _folderPicker.PickFolderAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputPath = path;
            StatusMessage = $"已選擇輸出資料夾：{path}";
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
        // 驗證工單是否已選擇
        if (_currentWorkOrderId == null)
        {
            _logger?.LogWarning("未選擇工單");
            StatusMessage = "請先選擇工單才能開始批量推論。";
            System.Windows.MessageBox.Show(
                "請先選擇工單才能開始批量推論。",
                "提示",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
        {
            _logger?.LogWarning("無效的輸入資料夾路徑: {RootPath}", RootPath);
            StatusMessage = "請選擇有效的輸入影像資料夾路徑。";
            return;
        }

        // 如果沒有指定輸出路徑，使用輸入路徑下的 _output 資料夾
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = Path.Combine(RootPath, "_output");
        }

        _logger?.LogInformation("開始批量推論 - 輸入資料夾: {RootPath}, 輸出資料夾: {OutputPath}", RootPath, OutputPath);

        if (_processingTask is { IsCompleted: false })
        {
            _logger?.LogWarning("批量推論已在執行中");
            return;
        }

        // 根據目前模型設定推論端點
        await ConfigureInferenceEndpointAsync();

        var files = await LoadFileListAsync(CancellationToken.None);
        _logger?.LogInformation("找到 {Count} 個檔案待處理", files.Count);
        
        if (files.Count == 0)
        {
            _logger?.LogWarning("資料夾中沒有可處理的影像: {RootPath}", RootPath);
            StatusMessage = "資料夾中沒有可處理的影像。";
            return;
        }

        InitializeStats();

        ResetCounters(files.Count);

        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsPaused = false;
        StatusMessage = "開始批量推論...";
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

    private void InitializeStats()
    {
        _dispatcher.Invoke(() =>
        {
            Stats.Clear();
            _statsLookup.Clear();

            // 動態從當前模型載入結果類型
            _ = InitializeStatsFromModelAsync();
        });
    }

    /// <summary>
    /// 根據目前選定的模型配置推論端點
    /// </summary>
    private async Task ConfigureInferenceEndpointAsync()
    {
        try
        {
            var currentModel = await _modelConfigService.GetCurrentModelAsync();
            if (currentModel == null)
            {
                _logger?.LogWarning("沒有選定的模型，使用預設推論端點");
                return;
            }

            // 檢查 _ai 是否為 HttpBatchInferencePort
            if (_ai is HttpBatchInferencePort batchPort)
            {
                if (currentModel.InferenceType == Models.InferenceType.Workflow)
                {
                    // Workflow 模式
                    batchPort.SetWorkflowEndpoint(currentModel.EdgeHubHost, currentModel.WorkflowPort);
                    _logger?.LogInformation("批量推論已設定為 Workflow 模式: {Host}:{Port}",
                        currentModel.EdgeHubHost, currentModel.WorkflowPort);
                }
                else
                {
                    // 單一模型模式
                    batchPort.SetSingleModelEndpoint(currentModel.EdgeHubHost, currentModel.InferencePort);
                    _logger?.LogInformation("批量推論已設定為單一模型模式: {Host}:{Port}",
                        currentModel.EdgeHubHost, currentModel.InferencePort);
                }
            }
            else
            {
                _logger?.LogWarning("批量推論 Port 不是 HttpBatchInferencePort 類型，無法動態設定端點");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "配置推論端點時發生錯誤");
        }
    }

    /// <summary>
    /// 從當前模型配置動態初始化統計項目
    /// </summary>
    private async Task InitializeStatsFromModelAsync()
    {
        try
        {
            var currentModel = await _modelConfigService.GetCurrentModelAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                // 添加模型定義的結果類型
                if (currentModel != null)
                {
                    var resultTypes = currentModel.GetEffectiveResultTypes();
                    foreach (var typeName in resultTypes)
                    {
                        var displayName = currentModel.GetDisplayName(typeName);
                        AddStatWithDisplayName(typeName, displayName);
                    }
                }

                // 確保有 Uncategorized 和 Error 類型
                AddStat(UncategorizedLabel);
                AddStat(ErrorLabel);
            });
        }
        catch
        {
            // 錯誤時使用預設值
            await _dispatcher.InvokeAsync(() =>
            {
                AddStat(UncategorizedLabel);
                AddStat(ErrorLabel);
            });
        }
    }

    private void AddStatWithDisplayName(string label, string displayName)
    {
        if (_statsLookup.ContainsKey(label))
        {
            return;
        }

        var vm = new DefectStatViewModel(displayName);
        _statsLookup[label] = vm;
        Stats.Add(vm);
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

            var fileName = Path.GetFileName(file);
            _logger?.LogInformation("開始處理圖片: {FileName}", fileName);

            try
            {
                _logger?.LogDebug("載入圖片: {FileName}", fileName);
                var image = await _loader.LoadAsync(file, ct);
                _logger?.LogDebug("圖片載入成功: {FileName}, 大小: {Width}x{Height}, 資料大小: {Size} bytes", 
                    fileName, image.Width, image.Height, image.Bytes.Length);

                _logger?.LogInformation("發送推論請求: {FileName}", fileName);
                
                // 如果 _ai 是 HttpBatchInferencePort，直接從文件讀取原始字節（避免圖片解碼問題）
                AiResult result;
                if (_ai is HttpBatchInferencePort httpBatchPort)
                {
                    _logger?.LogDebug("使用 PredictFromFileAsync 方法（直接讀取原始文件字節）");
                    result = await httpBatchPort.PredictFromFileAsync(file, ct);
                }
                else
                {
                    result = await _ai.PredictAsync(image, ct);
                }
                
                _logger?.LogInformation("推論完成: {FileName}, 分類: {Class}, 信心分數: {Confidence}", 
                    fileName, result.PredictedClass ?? "null", result.Confidence ?? 0d);

                // 更新當前推論結果
                CurrentLabel = result.PredictedClass ?? UncategorizedLabel;
                CurrentConfidence = result.Confidence ?? 0d;

                await UpdateStatsAsync(CurrentLabel);
                await UpdatePreviewAsync(image, file);

                // 儲存照片和 JSON
                _logger?.LogDebug("儲存結果: {FileName}", fileName);
                await SaveResultAsync(file, image, result, ct);
                _logger?.LogInformation("處理完成: {FileName}", fileName);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("處理被取消: {FileName}", fileName);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "處理圖片時發生錯誤: {FileName}, 錯誤訊息: {Message}, 堆疊追蹤: {StackTrace}", 
                    fileName, ex.Message, ex.StackTrace);
                await UpdateStatsAsync(ErrorLabel);
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"處理 {fileName} 時發生錯誤：{ex.Message}";
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
    /// 儲存推論結果（照片和 JSON）
    /// </summary>
    private async Task SaveResultAsync(string sourceFile, ImageData image, AiResult result, CancellationToken ct)
    {
        // 確定分類標籤
        var label = result.PredictedClass ?? UncategorizedLabel;

        // 建立輸出資料夾結構：{OutputPath}/{Label}/
        var destinationFolder = Path.Combine(OutputPath, label);
        Directory.CreateDirectory(destinationFolder);

        var fileName = Path.GetFileNameWithoutExtension(sourceFile);
        var extension = Path.GetExtension(sourceFile);

        // 1. 儲存照片
        var imagePath = Path.Combine(destinationFolder, $"{fileName}{extension}");
        await _writer.SaveAsync(imagePath, image, ct);

        // 2. 儲存 JSON（包含推論結果）
        var jsonPath = Path.Combine(destinationFolder, $"{fileName}.json");
        await SaveJsonAsync(jsonPath, result, sourceFile, ct);

        // 3. 儲存檢測記錄到數據庫（關聯到工單）
        if (_currentWorkOrderId.HasValue)
        {
            try
            {
                var inspection = new Inspection(
                    workOrderId: _currentWorkOrderId.Value,
                    result: label,
                    confidence: (float?)result.Confidence,
                    imagePath: imagePath,
                    modelVersion: "BatchInference",
                    annotatedImagePath: null,
                    inferenceTimeMs: null,
                    defects: null);

                await _inspectionRepository.AddAsync(inspection, ct);
                _logger?.LogInformation("檢測記錄已保存到數據庫: {FileName}, 工單: {WorkOrderId}", fileName, _currentWorkOrderId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存檢測記錄到數據庫失敗: {FileName}", fileName);
                // 不中斷處理流程，繼續處理下一張圖片
            }
        }
    }

    /// <summary>
    /// 儲存 JSON 檔案
    /// </summary>
    private async Task SaveJsonAsync(string jsonPath, AiResult result, string sourceFile, CancellationToken ct)
    {
        var jsonData = new
        {
            source_file = sourceFile,
            predicted_class = result.PredictedClass,
            confidence = result.Confidence,
            inference_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            raw_response = result.RawJson != null ? JsonDocument.Parse(result.RawJson) : null
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(jsonData, options);
        await File.WriteAllTextAsync(jsonPath, json, ct);
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

