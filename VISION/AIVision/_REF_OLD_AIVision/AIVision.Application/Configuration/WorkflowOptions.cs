using System.ComponentModel.DataAnnotations;

namespace AIVision.Application.Configuration;

/// <summary>
/// AINAVI Workflow 服務配置選項
/// </summary>
public sealed class WorkflowOptions
{
    public const string SectionName = "Workflow";

    /// <summary>
    /// 是否啟用 Workflow 模式 (true=使用 Workflow API, false=使用傳統 inference API)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// EdgeHub 主機位址 (例如 127.0.0.1 或 192.168.1.95)
    /// </summary>
    [Required]
    public string EdgeHubHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// EdgeHub 服務埠號 (預設 5001)
    /// </summary>
    [Range(1, 65535)]
    public int EdgeHubPort { get; set; } = 5001;

    /// <summary>
    /// Workflow 推論服務埠號 (預設 11000)
    /// </summary>
    [Range(1, 65535)]
    public int WorkflowPort { get; set; } = 11000;

    /// <summary>
    /// workflow_setting.json 檔案路徑
    /// </summary>
    public string WorkflowSettingPath { get; set; } = "";

    /// <summary>
    /// HTTP 請求超時秒數
    /// </summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 取得 EdgeHub 管理 API URL
    /// </summary>
    public string GetEdgeHubUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}";

    /// <summary>
    /// 取得 Workflow 推論 API URL
    /// </summary>
    public string GetWorkflowRunUrl() => $"http://{EdgeHubHost}:{WorkflowPort}/workflow/run";

    /// <summary>
    /// 取得 Workflow 服務啟動 API URL
    /// </summary>
    public string GetWorkflowServiceUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}/service/workflow";

    /// <summary>
    /// 取得服務查詢 API URL
    /// </summary>
    public string GetServiceQueryUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}/service?service_type=workflow";

    /// <summary>
    /// 取得服務關閉 API URL
    /// </summary>
    public string GetServicesDeleteUrl() => $"http://{EdgeHubHost}:{EdgeHubPort}/services";
}
