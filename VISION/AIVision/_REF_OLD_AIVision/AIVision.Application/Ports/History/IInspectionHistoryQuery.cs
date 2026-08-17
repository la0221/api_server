namespace AIVision.Application.Ports.History;

/// <summary>
/// 檢測歷史查詢接口
/// </summary>
public interface IInspectionHistoryQuery
{
    /// <summary>查詢檢測歷史記錄</summary>
    Task<PagedResult<InspectionHistoryDto>> QueryAsync(
        InspectionQueryFilter filter,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>取得單筆檢測記錄詳情</summary>
    Task<InspectionDetailDto?> GetDetailAsync(Guid inspectionId, CancellationToken cancellationToken);

    /// <summary>取得工單列表（用於篩選）</summary>
    Task<IReadOnlyList<string>> GetWorkOrderCodesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 檢測查詢篩選條件
/// </summary>
public sealed class InspectionQueryFilter
{
    public string? WorkOrderCode { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Result { get; set; }  // OK, NG, or null for all
    public string? DefectType { get; set; }
}

/// <summary>
/// 檢測歷史列表 DTO
/// </summary>
public sealed class InspectionHistoryDto
{
    public Guid Id { get; init; }
    public string WorkOrderCode { get; init; } = string.Empty;
    public DateTime InspectedAt { get; init; }
    public string Result { get; init; } = string.Empty;
    public float? Confidence { get; init; }
    public string? ImagePath { get; init; }
    public string? AnnotatedImagePath { get; init; }
    public int DefectCount { get; init; }
}

/// <summary>
/// 檢測詳細資料 DTO
/// </summary>
public sealed class InspectionDetailDto
{
    public Guid Id { get; init; }
    public string WorkOrderCode { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string? ModelName { get; init; }
    public DateTime InspectedAt { get; init; }
    public string Result { get; init; } = string.Empty;
    public float? Confidence { get; init; }
    public string? ImagePath { get; init; }
    public string? AnnotatedImagePath { get; init; }
    public int? InferenceTimeMs { get; init; }
    public IReadOnlyList<DefectDetailDto> Defects { get; init; } = Array.Empty<DefectDetailDto>();
}

/// <summary>
/// 瑕疵詳細資料 DTO
/// </summary>
public sealed class DefectDetailDto
{
    public string Type { get; init; } = string.Empty;
    public float? Confidence { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Severity { get; init; }
}

/// <summary>
/// 分頁結果
/// </summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
    public int PageIndex { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;
}
