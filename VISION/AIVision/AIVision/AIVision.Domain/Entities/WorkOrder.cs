using AIVision.Domain.Abstractions;

namespace AIVision.Domain.Entities;

/// <summary>
/// 生產工單資訊，掌握檢測批次生命週期。
/// </summary>
public sealed class WorkOrder : Entity<Guid>
{
    public WorkOrder(
        string code,
        string productName,
        string? modelName = null,
        string? machineModelName = null,
        Guid? id = null,
        DateTime? startAtUtc = null,
        string? expectedMoldCode = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("工單代碼不可為空白。", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(productName))
        {
            throw new ArgumentException("產品名稱不可為空白。", nameof(productName));
        }

        Code = code;
        ProductName = productName;
        ModelName = modelName;
        MachineModelName = machineModelName;
        ExpectedMoldCode = expectedMoldCode;
        Status = "Active";
        Id = id ?? Guid.NewGuid();
        StartAt = startAtUtc ?? DateTime.UtcNow;
    }

    public string Code { get; }

    public string ProductName { get; private set; }

    public string? ModelName { get; private set; }

    public string? MachineModelName { get; private set; }

    /// <summary>操作員預期模號(完整碼,例 "M101/07";null/空 = 不做模號核對)。</summary>
    public string? ExpectedMoldCode { get; private set; }

    public string Status { get; private set; }

    public DateTime StartAt { get; private set; }

    public DateTime? EndAt { get; private set; }

    public bool IsClosed => EndAt.HasValue;

    public void Close(DateTime? closedAtUtc = null)
    {
        if (IsClosed)
        {
            return;
        }

        EndAt = closedAtUtc ?? DateTime.UtcNow;
        Status = "Completed";
    }

    public void UpdateModelName(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("模型名稱不可為空白。", nameof(modelName));
        }

        ModelName = modelName;
    }

    /// <summary>編輯工單基本資料（產品名、機種/批次、預期模號）。工單代碼不可改。</summary>
    public void UpdateDetails(string productName, string? machineModelName, string? expectedMoldCode)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            throw new ArgumentException("產品名稱不可為空白。", nameof(productName));
        }

        ProductName = productName;
        MachineModelName = machineModelName;
        ExpectedMoldCode = string.IsNullOrWhiteSpace(expectedMoldCode) ? null : expectedMoldCode;
    }

    public void UpdateStatus(string status)
    {
        if (!new[] { "Active", "Completed", "Cancelled" }.Contains(status))
        {
            throw new ArgumentException("無效的狀態，必須是 Active、Completed 或 Cancelled。", nameof(status));
        }

        Status = status;
    }
}
