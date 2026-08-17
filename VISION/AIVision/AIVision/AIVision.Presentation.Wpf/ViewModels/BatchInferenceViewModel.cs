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
using AIVision.Application.Ports.ImageBatch;
using AIVision.Application.Ports.MoldCode;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Services;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using AIVision.Presentation.Wpf.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InspectionEntity = AIVision.Domain.Entities.Inspection;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 批量推論 ViewModel — 本地 ONNX 模號辨識(取代舊 AINAVI HTTP)。
/// 流程：選模型 + 工單 + 輸入資料夾 → 逐張本地辨識 → 依讀碼分類存圖/JSON + 寫檢測記錄(關聯工單)。
/// 辨識在背景執行緒跑(避免凍 UI);辨識器為可切換的本地 ONNX(<see cref="IMoldCodeModelSwitch"/>)。
/// </summary>
public partial class BatchInferenceViewModel : ObservableObject, IAsyncDisposable
{
    private const string UncategorizedLabel = "(none)";
    private const string ErrorLabel = "Error";

    private readonly IFolderPickerPort _folderPicker;
    private readonly IImageEnumeratorPort _enumerator;
    private readonly IImageWriterPort _writer;
    private readonly IMessenger _messenger;
    private readonly IWorkOrderManagementService _workOrderService;
    private readonly IInspectionRepository _inspectionRepository;
    private readonly INavigationService _navigationService;
    private readonly ModelConfigService _modelConfigService;
    private readonly IMoldCodeRecognizerPort _recognizer;
    private readonly IMoldCodeModelSwitch? _modelSwitch;
    private readonly MoldCodeOnnxOptions _onnx;
    private readonly IMoldCodePairModelSwitch _pairSwitch;
    private readonly MoldCodeWarpPolarOptions _warpOpts;
    private readonly PairWorkflowState _state;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<BatchInferenceViewModel>? _logger;

    private const string PairsRoot = @"D:\AIVisionModels\pairs";
    private string? _currentExpectedCode;

    private readonly Dictionary<string, DefectStatViewModel> _statsLookup = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private readonly object _pauseLock = new();
    private TaskCompletionSource<bool>? _resumeTcs;
    private bool _pauseRequested;
    private Guid? _currentWorkOrderId;
    private IReadOnlyList<string> _classSet = Array.Empty<string>();

    public BatchInferenceViewModel(
        IFolderPickerPort folderPicker,
        IImageEnumeratorPort enumerator,
        IImageWriterPort writer,
        IMessenger messenger,
        IWorkOrderManagementService workOrderService,
        IInspectionRepository inspectionRepository,
        INavigationService navigationService,
        ModelConfigService modelConfigService,
        IMoldCodeRecognizerPort recognizer,
        IOptions<MoldCodeOnnxOptions> onnxOptions,
        IMoldCodePairModelSwitch pairSwitch,
        IOptions<MoldCodeWarpPolarOptions> warpOptions,
        PairWorkflowState state,
        IMoldCodeModelSwitch? modelSwitch = null,
        ILogger<BatchInferenceViewModel>? logger = null)
    {
        _folderPicker = folderPicker;
        _enumerator = enumerator;
        _writer = writer;
        _messenger = messenger;
        _workOrderService = workOrderService;
        _inspectionRepository = inspectionRepository;
        _navigationService = navigationService;
        _modelConfigService = modelConfigService;
        _recognizer = recognizer;
        _onnx = onnxOptions.Value;
        _pairSwitch = pairSwitch;
        _warpOpts = warpOptions.Value;
        _state = state;
        _modelSwitch = modelSwitch;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _logger = logger;

        AvailableModels = new ObservableCollection<ModelConfig>();
        StatusMessage = "尚未開始";

        // 承接「模號穴號模型管理」頁：目前雙 head 版本 + 上次選的影像資料夾，避免重複選。
        RefreshPairModelDisplay();
        if (string.IsNullOrWhiteSpace(RootPath) && !string.IsNullOrWhiteSpace(_state.LastImageFolder))
            RootPath = _state.LastImageFolder!;

        // 訂閱工單變更訊息
        _messenger.Register<WorkOrderChangedMessage>(this, async (_, _) =>
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                await LoadCurrentWorkOrderAsync();
            });
        });

        _ = LoadCurrentWorkOrderAsync();
        _ = LoadModelsAsync();
    }

    public ObservableCollection<DefectStatViewModel> Stats { get; } = new();

    /// <summary>可選用的本地 ONNX 模型(僅 ModelPath 以 .onnx 結尾者)。</summary>
    public ObservableCollection<ModelConfig> AvailableModels { get; }

    [ObservableProperty]
    private ModelConfig? selectedModel;

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

    /// <summary>目前工單的預期模號（雙 head 核對用；空 = 只辨識不核對）。</summary>
    [ObservableProperty]
    private string expectedCodeDisplay = "—";

    /// <summary>目前使用的雙 head 模型版本 + 實際權重路徑（承接自模型管理頁；唯讀顯示）。</summary>
    [ObservableProperty]
    private string pairModelDisplay = "（未載入雙 head 模型）";

    /// <summary>依「目前載入版本」刷新雙 head 模型顯示（版本 + 模號/穴號 .onnx 實際路徑）。</summary>
    private void RefreshPairModelDisplay()
    {
        var v = _pairSwitch.CurrentVersionName;
        var paths = ResolvePairPaths();
        PairModelDisplay = paths is null
            ? "（未載入雙 head 模型；請先到「模號穴號模型管理」載入版本）"
            : $"版本 {v ?? "baseline"}　|　模號: {paths.Value.mohao}　|　穴號: {paths.Value.xuehao}";
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var all = await _modelConfigService.GetAllModelsAsync();
            var onnxModels = all
                .Where(m => !string.IsNullOrWhiteSpace(m.ModelPath) &&
                            m.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                .ToList();

            AvailableModels.Clear();
            foreach (var m in onnxModels)
                AvailableModels.Add(m);

            var current = await _modelConfigService.GetCurrentModelAsync();
            SelectedModel = (current != null
                ? AvailableModels.FirstOrDefault(m =>
                    string.Equals(m.OriginalName, current.OriginalName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Name, current.Name, StringComparison.OrdinalIgnoreCase))
                : null) ?? AvailableModels.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "載入本地 ONNX 模型清單失敗");
        }
    }

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
                _currentExpectedCode = workOrder.ExpectedMoldCode;
                ExpectedCodeDisplay = string.IsNullOrWhiteSpace(_currentExpectedCode)
                    ? "（工單未設預期模號 → 只辨識不核對）"
                    : _currentExpectedCode!;
            }
            else
            {
                _currentWorkOrderId = null;
                CurrentWorkOrderCode = "無工單";
                CurrentWorkOrderProduct = "未設定";
                _currentExpectedCode = null;
                ExpectedCodeDisplay = "—";
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
        _navigationService.ShowDialog<Views.WorkOrderManagementView>();
    }

    /// <summary>下一步：直接前往「歷史圖庫」看剛跑的核對結果 / 混料，不必回選單。</summary>
    [RelayCommand]
    private void OpenHistory() => _navigationService.ShowWindow<Views.HistoryView>();

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

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = Path.Combine(RootPath, "_output");
        }

        _logger?.LogInformation("開始批量辨識 - 輸入: {RootPath}, 輸出: {OutputPath}", RootPath, OutputPath);

        if (_processingTask is { IsCompleted: false })
        {
            _logger?.LogWarning("批量辨識已在執行中");
            return;
        }

        // 雙 head 模號穴號核對：辨識器於 ProcessFilesAsync 內依「目前載入版本」建立（無相機 ROI）。
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
        StatusMessage = "開始批量辨識...";
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

    /// <summary>
    /// 載入使用者選定(或當前)的本地 ONNX 模型到可切換辨識器，並以該模型類別組成封閉字集。
    /// </summary>
    private void ApplySelectedModel()
    {
        var model = SelectedModel;
        if (model != null &&
            !string.IsNullOrWhiteSpace(model.ModelPath) &&
            model.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) &&
            model.ResultTypes is { Count: > 0 })
        {
            try
            {
                _modelSwitch?.LoadModel(model.ModelPath, model.ResultTypes, _onnx.CodePrefix);
                _logger?.LogInformation("批量辨識使用模型: {Model} ({Path})", model.Name, model.ModelPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "切換批量辨識模型失敗，沿用目前模型: {Path}", model.ModelPath);
            }
            _classSet = model.ResultTypes.Select(c => $"{_onnx.CodePrefix}/{c}").ToList();
        }
        else
        {
            // 未選或無類別 → 沿用 baseline 字集
            _classSet = _onnx.ClassNames.Select(c => $"{_onnx.CodePrefix}/{c}").ToList();
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

            // 以選定模型的封閉字集(完整碼 prefix/cavity)作為統計分類
            foreach (var code in _classSet)
            {
                AddStat(code);
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
        var paths = ResolvePairPaths();
        if (paths is null)
        {
            await _dispatcher.InvokeAsync(() =>
                StatusMessage = "找不到雙 head 模型。請先在「模號穴號模型管理 (雙head)」頁載入版本。");
            return;
        }

        var version = _pairSwitch.CurrentVersionName ?? "pair";
        var expected = _currentExpectedCode;
        var (expMohao, expXuehao) = SplitExpected(expected);
        bool doVerify = !string.IsNullOrWhiteSpace(expected);
        int passes = _warpOpts.Passes <= 0 ? 2 : _warpOpts.Passes;

        await _dispatcher.InvokeAsync(() =>
            StatusMessage = $"初始化雙 head 辨識器（{version}，無相機 ROI）…");
        // 離線圖已裁 → 用預設前處理(RoiW=0)，對齊 harness/訓練端；避免相機 ROI 裁錯 → 誤判。
        using var rec = await Task.Run(
            () => new WarpPolarTwoHeadRecognizer(paths.Value.mohao, paths.Value.xuehao, new WarpPolarParams(), passes), ct);

        var stopwatch = Stopwatch.StartNew();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct);

            var fileName = Path.GetFileName(file);

            try
            {
                var image = await Task.Run(() => MoldCodeImageLoader.LoadFromFile(file), ct);
                var pair = await Task.Run(() => ((IMoldCodePairRecognizerPort)rec).Recognize(image), ct);

                string readCode = pair.HasReading ? $"{pair.Mohao}/{pair.Xuehao}" : UncategorizedLabel;
                double confidence = pair.HasReading ? Math.Min(pair.ConfMohao, pair.ConfXuehao) : 0d;

                // 驗證：有預期碼才核對 → Match / MixedAlarm(混料)；否則只辨識(Read/NoRead)。
                string outcome;
                bool isOk, airBlown;
                if (!pair.HasReading) { outcome = "NoRead"; isOk = false; airBlown = false; }
                else if (!doVerify) { outcome = "Read"; isOk = true; airBlown = false; }
                else
                {
                    bool mOk = Eq(pair.Mohao, expMohao);
                    bool xOk = Eq(pair.Xuehao, expXuehao);
                    isOk = mOk && xOk;
                    outcome = isOk ? "Match" : "MixedAlarm";
                    airBlown = !isOk;
                }

                CurrentLabel = readCode;
                CurrentConfidence = double.IsFinite(confidence) ? confidence : 0d;

                await UpdateStatsAsync(outcome);
                await UpdatePreviewAsync(image, file);
                await SaveResultAsync(file, readCode, CurrentConfidence, expected, outcome, isOk, airBlown, version, ct);

                _logger?.LogInformation("辨識完成: {FileName} → {Read} / {Outcome} (conf={Conf:F2})",
                    fileName, readCode, outcome, CurrentConfidence);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("處理被取消: {FileName}", fileName);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "處理圖片時發生錯誤: {FileName}", fileName);
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

    /// <summary>把目前載入版本解析成一組 (mohao,xuehao) .onnx 路徑；找不到版本夾 → 退回 appsettings baseline。</summary>
    private (string mohao, string xuehao)? ResolvePairPaths()
    {
        var v = _pairSwitch.CurrentVersionName;
        if (!string.IsNullOrWhiteSpace(v) && !string.Equals(v, "baseline", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(PairsRoot, v);
            var mo = Path.Combine(dir, "mohao.onnx");
            var xu = Path.Combine(dir, "xuehao.onnx");
            if (File.Exists(mo) && File.Exists(xu)) return (mo, xu);
        }
        if (File.Exists(_warpOpts.MohaoModelPath) && File.Exists(_warpOpts.XuehaoModelPath))
            return (_warpOpts.MohaoModelPath, _warpOpts.XuehaoModelPath);
        return null;
    }

    /// <summary>拆解預期完整碼 "M101/07" → (模號 "M101", 穴號 "07")。</summary>
    private static (string mohao, string xuehao) SplitExpected(string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return ("", "");
        var parts = expected.Split(new[] { '/', '-', '_', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
    }

    private static bool Eq(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>儲存辨識結果（照片 + JSON），並寫一筆雙 head 核對記錄到資料庫（關聯工單，供歷史圖庫）。</summary>
    private async Task SaveResultAsync(
        string sourceFile, string readCode, double confidence,
        string? expectedCode, string outcome, bool isOk, bool airBlown, string version, CancellationToken ct)
    {
        // 依核對結果分類到輸出子資料夾（Match / MixedAlarm / NoRead …）。
        var safeLabel = string.Join("_", outcome.Split(Path.GetInvalidFileNameChars()));
        var destinationFolder = Path.Combine(OutputPath, safeLabel);
        Directory.CreateDirectory(destinationFolder);

        var fileName = Path.GetFileNameWithoutExtension(sourceFile);
        var extension = Path.GetExtension(sourceFile);

        var imagePath = Path.Combine(destinationFolder, $"{fileName}{extension}");
        // 直接複製原始檔(不 decode→重編碼)，保位元一致、零畫質損失；同路徑則跳過。
        var sameFile = string.Equals(
            Path.GetFullPath(sourceFile), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase);
        if (!sameFile)
            await Task.Run(() => File.Copy(sourceFile, imagePath, overwrite: true), ct);

        var jsonPath = Path.Combine(destinationFolder, $"{fileName}.json");
        await SaveJsonAsync(jsonPath, readCode, confidence, sourceFile, ct);

        if (_currentWorkOrderId.HasValue)
        {
            try
            {
                var inspection = new InspectionEntity(
                    workOrderId: _currentWorkOrderId.Value,
                    result: isOk ? "OK" : "NG",
                    confidence: (float)confidence,
                    imagePath: imagePath,
                    modelVersion: version,
                    expectedCode: expectedCode,
                    readCode: readCode == UncategorizedLabel ? null : readCode,
                    outcome: outcome,
                    airBlown: airBlown);

                await _inspectionRepository.AddAsync(inspection, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存檢測記錄到資料庫失敗: {FileName}", fileName);
            }
        }
    }

    private async Task SaveJsonAsync(string jsonPath, string code, double confidence, string sourceFile, CancellationToken ct)
    {
        var jsonData = new
        {
            source_file = sourceFile,
            read_code = code,
            confidence,
            inference_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
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
