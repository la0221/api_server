namespace AIVision.Application.Services;

using AIVision.Domain.Entities;

/// <summary>
/// 工單管理服務，負責當前工單的生命週期管理
/// </summary>
public interface IWorkOrderManagementService
{
    /// <summary>取得當前活動工單</summary>
    Task<WorkOrder?> GetCurrentWorkOrderAsync(CancellationToken cancellationToken);

    /// <summary>創建新工單並設為當前工單</summary>
    Task<WorkOrder> CreateWorkOrderAsync(
        string productName,
        string? modelName,
        string? machineModelName,
        CancellationToken cancellationToken);

    /// <summary>創建新工單並設為當前工單（支持自訂工單號）</summary>
    Task<WorkOrder> CreateWorkOrderAsync(
        string productName,
        string? modelName,
        string? machineModelName,
        string? customWorkOrderCode,
        CancellationToken cancellationToken);

    /// <summary>創建新工單並設為當前工單（支持自訂工單號 + 操作員預期模號）</summary>
    Task<WorkOrder> CreateWorkOrderAsync(
        string productName,
        string? modelName,
        string? machineModelName,
        string? customWorkOrderCode,
        string? expectedMoldCode,
        CancellationToken cancellationToken);

    /// <summary>編輯既有工單基本資料（產品名/機種批次/預期模號；工單代碼不可改）。若為目前工單則同步更新記憶體。</summary>
    Task UpdateWorkOrderAsync(
        Guid workOrderId,
        string productName,
        string? machineModelName,
        string? expectedMoldCode,
        CancellationToken cancellationToken);

    /// <summary>結束當前工單</summary>
    Task EndCurrentWorkOrderAsync(CancellationToken cancellationToken);

    /// <summary>切換到指定工單（結束當前工單）</summary>
    Task SwitchToWorkOrderAsync(string workOrderCode, CancellationToken cancellationToken);
}
