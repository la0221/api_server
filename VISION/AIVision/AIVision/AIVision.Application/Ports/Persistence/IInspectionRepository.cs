using InspectionEntity = AIVision.Domain.Entities.Inspection;

namespace AIVision.Application.Ports.Persistence;

/// <summary>
/// 工單檢測統計資料
/// </summary>
public record InspectionStatistics(int TotalCount, int OkCount, int NgCount);

public interface IInspectionRepository
{
    Task AddAsync(InspectionEntity inspection, CancellationToken cancellationToken);

    /// <summary>
    /// 取得指定工單的檢測統計
    /// </summary>
    Task<InspectionStatistics> GetStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// 取得指定工單的瑕疵類型統計（用於載入工單時還原瑕疵統計面板）
    /// </summary>
    /// <returns>Dictionary: Key=瑕疵類型, Value=該類型數量</returns>
    Task<Dictionary<string, int>> GetDefectStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken);
}
