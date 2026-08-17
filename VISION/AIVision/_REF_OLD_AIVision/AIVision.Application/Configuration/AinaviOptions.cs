using System.ComponentModel.DataAnnotations;

namespace AIVision.Application.Configuration;

/// <summary>
/// AINAVI EdgeHub 服務配置選項。
/// </summary>
public sealed class AinaviOptions
{
    /// <summary>
    /// AINAVI EdgeHub 主機位址，例如 http://192.168.1.95
    /// </summary>
    [Required]
    [Url]
    public string Host { get; set; } = "http://192.168.1.95";

    /// <summary>
    /// EdgeHub 服務埠號（預設 5001）
    /// </summary>
    [Range(1, 65535)]
    public int EdgeHubPort { get; set; } = 5001;

    /// <summary>
    /// 模型基礎路徑，例如 /home/smasoft/Public/ainavi_edgehub/models
    /// </summary>
    [Required]
    public string ModelBasePath { get; set; } = "/home/smasoft/Public/ainavi_edgehub/models";

    /// <summary>
    /// 預設模型名稱（預設 WPC_top）
    /// </summary>
    public string DefaultModel { get; set; } = "WPC_top";

    /// <summary>
    /// 預設模型埠號（預設 8009）
    /// </summary>
    [Range(1, 65535)]
    public int DefaultModelPort { get; set; } = 8009;

    /// <summary>
    /// 推論記錄檔路徑（預設 logs/inference_log.json）
    /// </summary>
    public string LogPath { get; set; } = "logs/inference_log.json";

    /// <summary>
    /// 取得模型完整路徑。
    /// </summary>
    /// <param name="modelName">模型名稱</param>
    /// <returns>完整路徑</returns>
    public string GetModelPath(string modelName) => $"{ModelBasePath.TrimEnd('/')}/{modelName}";

    /// <summary>
    /// 取得 EdgeHub 完整 URL。
    /// </summary>
    /// <returns>完整 URL</returns>
    public string GetEdgeHubUrl() => $"{Host.TrimEnd('/')}:{EdgeHubPort}";
}

