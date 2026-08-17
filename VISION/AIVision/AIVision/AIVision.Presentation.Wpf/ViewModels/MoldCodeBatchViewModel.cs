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
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 模號「離線 / 批量辨識」頁:對選定資料夾的影像跑本地 ONNX 模號辨識(完全離線,免相機/PLC)。
/// 若資料夾下有子資料夾(如 01..18),子資料夾名視為正解(ground truth),計算準確率/混淆。
/// 對齊 MoldCode.Harness MODE 1。
/// </summary>
public partial class MoldCodeBatchViewModel : ObservableObject
{
    private readonly IMoldCodeRecognizerPort _recognizer;
    private readonly IFolderPickerPort _folderPicker;
    private readonly MoldCodeOnnxOptions _onnx;
    private readonly ModelConfigService _modelConfigService;
    private readonly IMoldCodeModelSwitch? _modelSwitch;
    private readonly ILogger<MoldCodeBatchViewModel>? _logger;

    public MoldCodeBatchViewModel(
        IMoldCodeRecognizerPort recognizer,
        IFolderPickerPort folderPicker,
        IOptions<MoldCodeOnnxOptions> onnxOptions,
        ModelConfigService modelConfigService,
        IMoldCodeModelSwitch? modelSwitch = null,
        IOptions<Models.TestImageFolderOptions>? testFolders = null,
        ILogger<MoldCodeBatchViewModel>? logger = null)
    {
        _recognizer = recognizer;
        _folderPicker = folderPicker;
        _onnx = onnxOptions.Value;
        _modelConfigService = modelConfigService;
        _modelSwitch = modelSwitch;
        _logger = logger;
        TestFolderOptions = new ObservableCollection<string>(
            testFolders?.Value.Paths ?? new System.Collections.Generic.List<string>());
        Results = new ObservableCollection<MoldCodeBatchRow>();
        AvailableModels = new ObservableCollection<ModelConfig>();

        // 載入可選的本地 ONNX 模型清單(非同步,不阻塞 ctor)。
        _ = LoadModelsAsync();
    }

    /// <summary>可選用的本地 ONNX 模型(僅 ModelPath 以 .onnx 結尾者)。</summary>
    public ObservableCollection<ModelConfig> AvailableModels { get; }

    [ObservableProperty]
    private ModelConfig? selectedModel;

    /// <summary>
    /// 載入本地 ONNX 模型清單並預選目前模型。失敗只記錄,不阻斷批量頁。
    /// </summary>
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

    [ObservableProperty]
    private string? selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    private bool isRunning;

    [ObservableProperty]
    private string summary = "選擇資料夾後執行批量辨識。若有子資料夾(如 01..18),子資料夾名會當作正解(ground truth)計算準確率。";

    [ObservableProperty]
    private bool useSubfolderAsGroundTruth = true;

    public bool NotRunning => !IsRunning;

    public ObservableCollection<MoldCodeBatchRow> Results { get; }

    /// <summary>「測試資料夾」下拉的常用路徑選項（appsettings TestImageFolders；仍可貼任意路徑）。</summary>
    public ObservableCollection<string> TestFolderOptions { get; }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var folder = await _folderPicker.PickFolderAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(folder))
            SelectedFolder = folder;
    }

    [RelayCommand]
    private async Task RunBatchAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
        {
            Summary = "請先選擇有效的資料夾。";
            return;
        }
        if (IsRunning) return;

        IsRunning = true;
        Results.Clear();
        Summary = "辨識中…";

        // 執行前:若使用者選了模型,先抽換辨識器並以該模型的類別(穴號)組成封閉字集。
        List<string> classSet;
        var selected = SelectedModel;
        if (selected != null &&
            !string.IsNullOrWhiteSpace(selected.ModelPath) &&
            selected.ResultTypes is { Count: > 0 })
        {
            try
            {
                _modelSwitch?.LoadModel(selected.ModelPath, selected.ResultTypes, _onnx.CodePrefix);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "切換批量辨識模型失敗，沿用目前模型: {Path}", selected.ModelPath);
            }
            classSet = selected.ResultTypes.Select(c => $"{_onnx.CodePrefix}/{c}").ToList();
        }
        else
        {
            // 未選模型 → 沿用 baseline 字集。
            classSet = _onnx.ClassNames.Select(c => $"{_onnx.CodePrefix}/{c}").ToList();
        }

        var folder = SelectedFolder!;
        bool useTruth = UseSubfolderAsGroundTruth;

        try
        {
            var (rows, total, correct, p50, p95) = await Task.Run(() => RunCore(folder, useTruth, classSet));

            foreach (var r in rows)
                Results.Add(r);

            Summary = total > 0
                ? $"張數={rows.Count}　比對={total}　正確={correct}　準確率={(double)correct / total:P2}　|　單張 p50={p50:F1}ms p95={p95:F1}ms (CPU)"
                : $"張數={rows.Count}（無子資料夾正解，僅辨識）　|　單張 p50={p50:F1}ms p95={p95:F1}ms (CPU)";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "模號批量辨識失敗");
            Summary = $"失敗：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private (List<MoldCodeBatchRow> rows, int total, int correct, double p50, double p95) RunCore(
        string folder, bool useTruth, IReadOnlyList<string> classSet)
    {
        var rows = new List<MoldCodeBatchRow>();
        var times = new List<double>();
        int total = 0, correct = 0;

        var dirs = Directory.GetDirectories(folder);
        // 有子資料夾且啟用正解 → 每個子資料夾名為正解;否則整個資料夾平鋪(無正解)。
        IEnumerable<(string? truth, string dir)> groups =
            (useTruth && dirs.Length > 0)
                ? dirs.OrderBy(d => d, StringComparer.Ordinal)
                      .Select(d => ((string?)$"{_onnx.CodePrefix}/{Path.GetFileName(d)}", d))
                : new (string?, string)[] { (null, folder) };

        foreach (var (truth, dir) in groups)
        {
            foreach (var file in EnumerateImages(dir))
            {
                var sw = Stopwatch.StartNew();
                MarkingObservation obs;
                try
                {
                    var img = MoldCodeImageLoader.LoadFromFile(file);
                    obs = _recognizer.Recognize(img, classSet);
                }
                catch (Exception ex)
                {
                    obs = MarkingObservation.Failed(ex.Message);
                }
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);

                string read = obs.HasCode ? obs.Code! : "(none)";
                bool? match = null;
                if (!string.IsNullOrEmpty(truth))
                {
                    total++;
                    bool ok = obs.HasCode &&
                              MarkingVerifier.NormalizeCode(read) == MarkingVerifier.NormalizeCode(truth!);
                    if (ok) correct++;
                    match = ok;
                }

                rows.Add(new MoldCodeBatchRow(
                    Path.GetFileName(file), truth ?? "-", read, obs.ConfClassify, match));
            }
        }

        return (rows, total, correct, Percentile(times, 0.5), Percentile(times, 0.95));
    }

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

/// <summary>批量辨識單列結果(供 DataGrid 綁定)。</summary>
public sealed record MoldCodeBatchRow(string File, string Expected, string Read, double Confidence, bool? Match);
