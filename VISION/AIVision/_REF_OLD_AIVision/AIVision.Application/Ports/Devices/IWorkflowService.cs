namespace AIVision.Application.Ports.Devices;

/// <summary>
/// AINAVI Workflow 服務管理介面
/// </summary>
public interface IWorkflowService
{
    /// <summary>
    /// Workflow 服務是否運行中
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 當前 Workflow 服務埠號
    /// </summary>
    int? CurrentPort { get; }

    /// <summary>
    /// 啟動 Workflow 服務（使用配置的預設值）
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>啟動結果</returns>
    Task<WorkflowStartResult> StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 載入 Workflow 服務（使用指定參數，供專案初始化使用）
    /// </summary>
    /// <param name="edgeHubHost">EdgeHub 主機</param>
    /// <param name="edgeHubPort">EdgeHub 服務埠號</param>
    /// <param name="workflowPort">Workflow 推論埠號</param>
    /// <param name="workflowSettingPath">Workflow 設定檔路徑</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否成功載入</returns>
    Task<bool> LoadWorkflowAsync(
        string edgeHubHost,
        int edgeHubPort,
        int workflowPort,
        string workflowSettingPath,
        CancellationToken ct = default);

    /// <summary>
    /// 停止 Workflow 服務
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否成功停止</returns>
    Task<bool> StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 查詢 Workflow 服務狀態
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>服務狀態</returns>
    Task<WorkflowStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// 健康檢查
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>服務是否健康</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Workflow 服務啟動結果
/// </summary>
public sealed record WorkflowStartResult(
    bool Success,
    string Message,
    int? Port = null);

/// <summary>
/// Workflow 服務狀態
/// </summary>
public sealed record WorkflowStatus(
    bool IsRunning,
    string State,
    int? Port = null,
    string? Error = null);
