using System.Collections.Generic;

namespace AIVision.Presentation.Wpf.Models;

/// <summary>
/// 模型配置集合。
/// </summary>
public sealed class ModelsConfiguration
{
    /// <summary>
    /// 當前使用的模型名稱
    /// </summary>
    public string? CurrentModelName { get; set; }

    /// <summary>
    /// 模型列表（手動配置的模型，向後兼容）
    /// </summary>
    public List<ModelConfig> Models { get; set; } = new();

    /// <summary>
    /// 模型覆蓋設定（用於自動掃描模型的顯示名稱等自訂設定）
    /// </summary>
    public Dictionary<string, ModelOverride> Overrides { get; set; } = new();

    /// <summary>
    /// 手動模型列表（用於不在掃描資料夾中的模型）
    /// </summary>
    public List<ModelConfig> ManualModels { get; set; } = new();
}

/// <summary>
/// 模型覆蓋設定，用於自訂掃描到的模型顯示資訊
/// </summary>
public sealed class ModelOverride
{
    /// <summary>
    /// 自訂顯示名稱
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 自訂描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 類別的中文顯示名稱
    /// </summary>
    public Dictionary<string, string>? ClassDisplayNames { get; set; }

    /// <summary>
    /// 類別顏色（用於 UI 顯示）
    /// </summary>
    public Dictionary<string, string>? ClassColors { get; set; }

    /// <summary>
    /// EdgeHub 主機 IP
    /// </summary>
    public string? EdgeHubHost { get; set; }

    /// <summary>
    /// EdgeHub 服務埠號
    /// </summary>
    public int? EdgeHubPort { get; set; }

    /// <summary>
    /// 推論服務埠號
    /// </summary>
    public int? InferencePort { get; set; }

    /// <summary>
    /// 推論類型：SingleModel 或 Workflow
    /// </summary>
    public InferenceType? InferenceType { get; set; }

    /// <summary>
    /// Workflow 設定檔路徑
    /// </summary>
    public string? WorkflowSettingPath { get; set; }

    /// <summary>
    /// Workflow 推論服務埠號
    /// </summary>
    public int? WorkflowPort { get; set; }
}

