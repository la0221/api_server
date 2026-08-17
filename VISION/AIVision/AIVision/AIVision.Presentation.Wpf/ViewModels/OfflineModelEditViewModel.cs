using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 離線（本地 ONNX）模型「編輯模型」對話框 ViewModel。
/// <para>
/// 編輯單一本地模型的旁置設定 <c>&lt;name&gt;.config.json</c>（<see cref="MoldCodeModelConfig"/>）:
/// 基本(顯示名稱/說明/前綴)與進階前處理(Imgsz/Blackhat/Locator/OuterFactor)。
/// 載入時以「旁置設定 → baseline」順序填入欄位;儲存時寫回旁置檔。
/// 只讀顯示:該模型類別清單(來自 <c>.names.json</c>)與訓練/精度資訊(來自 <c>.report.json</c>)。
/// </para>
/// <para>
/// 完全本地、不涉及 AINAVI / EdgeHub;與 AINAVI 形狀的 <c>ModelEditViewModel</c> 無關。
/// </para>
/// </summary>
public partial class OfflineModelEditViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _modelPath;
    private readonly ILogger<OfflineModelEditViewModel>? _logger;

    /// <summary>關閉對話框請求(true=已儲存,false=取消)。由 View 訂閱以設定 DialogResult。</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>
    /// 建構子。
    /// </summary>
    /// <param name="model">被選取的本地模型(需含 .onnx 的 <see cref="ModelConfig.ModelPath"/>)。</param>
    /// <param name="baseline">baseline 前處理設定(欄位未在旁置檔設定時的預設值來源)。</param>
    /// <param name="logger">選用記錄器。</param>
    public OfflineModelEditViewModel(
        ModelConfig model,
        MoldCodeOnnxOptions baseline,
        ILogger<OfflineModelEditViewModel>? logger = null)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (baseline is null) throw new ArgumentNullException(nameof(baseline));

        _modelPath = model.ModelPath ?? string.Empty;
        _logger = logger;

        ModelFileName = Path.GetFileName(_modelPath);
        ModelPathDisplay = _modelPath;

        // 既有旁置設定(無則 null,欄位以 baseline 填入)。
        var sidecar = MoldCodeModelConfig.TryRead(_modelPath);

        DisplayName = sidecar?.DisplayName ?? model.Name ?? Path.GetFileNameWithoutExtension(_modelPath);
        Description = sidecar?.Description ?? model.Description ?? string.Empty;
        CodePrefix = string.IsNullOrWhiteSpace(sidecar?.CodePrefix) ? baseline.CodePrefix : sidecar!.CodePrefix!;
        Imgsz = sidecar?.Imgsz ?? baseline.Imgsz;
        UseBlackhat = sidecar?.UseBlackhat ?? baseline.UseBlackhat;
        UseLocator = sidecar?.UseLocator ?? baseline.UseLocator;
        OuterFactor = sidecar?.OuterFactor ?? baseline.OuterFactor;

        // 只讀:類別清單(.names.json)與訓練/精度資訊(.report.json / .train-report.json)。
        ClassList = new ObservableCollection<string>(ReadSiblingClassNames(_modelPath));
        ClassListSummary = ClassList.Count > 0
            ? $"{ClassList.Count} 類: {string.Join(", ", ClassList)}"
            : "（無 .names.json,辨識時將退回 baseline 類別）";
        TrainingInfo = ReadTrainingInfo(_modelPath) ?? "（無 .report.json / .train-report.json）";
    }

    // ===== 基本 =====

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string description = string.Empty;

    [ObservableProperty]
    private string codePrefix = string.Empty;

    // ===== 進階前處理(必須與訓練一致) =====

    [ObservableProperty]
    private int imgsz;

    [ObservableProperty]
    private bool useBlackhat;

    [ObservableProperty]
    private bool useLocator;

    [ObservableProperty]
    private double outerFactor;

    // ===== 只讀顯示 =====

    /// <summary>模型檔名(.onnx)。</summary>
    public string ModelFileName { get; }

    /// <summary>模型完整路徑(只讀顯示)。</summary>
    public string ModelPathDisplay { get; }

    /// <summary>該模型類別清單(來自 .names.json)。</summary>
    public ObservableCollection<string> ClassList { get; }

    /// <summary>類別清單彙整字串。</summary>
    public string ClassListSummary { get; }

    /// <summary>訓練/精度資訊(來自 .report.json,最佳努力)。</summary>
    public string TrainingInfo { get; }

    /// <summary>進階前處理警告文字(提醒須與訓練設定一致)。</summary>
    public string PreprocessWarning =>
        "前處理參數必須與該模型「訓練時」一致，改錯會大幅降低準確率（預設值=v3 訓練設定 320/blackhat/locator/1.5）。";

    [ObservableProperty]
    private string? errorMessage;

    /// <summary>
    /// 儲存:寫回旁置設定 <c>&lt;name&gt;.config.json</c>,成功則請求關閉(true)。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(_modelPath))
        {
            ErrorMessage = "模型路徑為空，無法儲存設定。";
            return;
        }

        if (Imgsz <= 0)
        {
            ErrorMessage = "Imgsz 必須為正整數。";
            return;
        }

        if (OuterFactor <= 0)
        {
            ErrorMessage = "OuterFactor 必須為正數。";
            return;
        }

        try
        {
            var cfg = new MoldCodeModelConfig
            {
                DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
                Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                CodePrefix = string.IsNullOrWhiteSpace(CodePrefix) ? null : CodePrefix.Trim(),
                Imgsz = Imgsz,
                UseBlackhat = UseBlackhat,
                UseLocator = UseLocator,
                OuterFactor = OuterFactor
            };

            MoldCodeModelConfig.Write(_modelPath, cfg);
            _logger?.LogInformation(
                "[OfflineEdit] 已寫入模型旁置設定: {Path} (Imgsz={Imgsz}, Blackhat={Bh}, Locator={Loc}, OuterFactor={Of}, Prefix={Prefix})",
                MoldCodeModelConfig.GetConfigPath(_modelPath), Imgsz, UseBlackhat, UseLocator, OuterFactor, CodePrefix);

            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"儲存失敗: {ex.Message}";
            _logger?.LogError(ex, "[OfflineEdit] 寫入旁置設定失敗: {Path}", _modelPath);
        }
    }

    /// <summary>取消:不寫入,請求關閉(false)。</summary>
    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    /// <summary>讀同名 .names.json,依索引排序回類別清單;失敗回空。</summary>
    private List<string> ReadSiblingClassNames(string modelPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return new List<string>();

            var namesPath = Path.ChangeExtension(modelPath, ".names.json");
            if (string.IsNullOrEmpty(namesPath) || !File.Exists(namesPath))
                return new List<string>();

            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(namesPath), JsonOptions)
                      ?? new Dictionary<string, string>();

            return raw
                .Where(kvp => int.TryParse(kvp.Key, out _) && !string.IsNullOrWhiteSpace(kvp.Value))
                .OrderBy(kvp => int.Parse(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[OfflineEdit] 讀取 names.json 失敗: {Path}", modelPath);
            return new List<string>();
        }
    }

    /// <summary>最佳努力讀 .report.json / .train-report.json,組訓練/精度摘要;無則回 null。</summary>
    private string? ReadTrainingInfo(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;

        var dir = Path.GetDirectoryName(modelPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(modelPath);

        foreach (var suffix in new[] { ".report.json", ".train-report.json" })
        {
            var reportPath = Path.Combine(dir, $"{name}{suffix}");
            if (!File.Exists(reportPath))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                var parts = new List<string>();
                foreach (var key in new[] { "val_accuracy", "ours_v3_acc", "accuracy", "acc" })
                {
                    if (root.TryGetProperty(key, out var el) &&
                        el.ValueKind == JsonValueKind.Number &&
                        el.TryGetDouble(out var acc))
                    {
                        parts.Add($"{key}={acc:P2}");
                    }
                }

                foreach (var key in new[] { "imgsz", "epochs", "model", "trained_at", "dataset" })
                {
                    if (root.TryGetProperty(key, out var el))
                    {
                        var val = el.ValueKind switch
                        {
                            JsonValueKind.String => el.GetString(),
                            JsonValueKind.Number => el.GetRawText(),
                            _ => null
                        };
                        if (!string.IsNullOrWhiteSpace(val))
                            parts.Add($"{key}={val}");
                    }
                }

                if (parts.Count > 0)
                    return $"[{name}{suffix}] " + string.Join(", ", parts);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[OfflineEdit] 讀取報告 {Report} 失敗", $"{name}{suffix}");
            }
        }

        return null;
    }
}
