using AIVision.Application.Ports.History;
using Dapper;

namespace AIVision.Infrastructure.Persistence.SQLite;

public sealed class SqliteInspectionHistoryQuery : IInspectionHistoryQuery
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public SqliteInspectionHistoryQuery(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<PagedResult<InspectionHistoryDto>> QueryAsync(
        InspectionQueryFilter filter,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var whereConditions = new List<string>();
        var parameters = new DynamicParameters();

        // 工單篩選
        if (!string.IsNullOrWhiteSpace(filter.WorkOrderCode))
        {
            whereConditions.Add("wo.Code = @WorkOrderCode");
            parameters.Add("WorkOrderCode", filter.WorkOrderCode);
        }

        // 日期範圍篩選
        if (filter.StartDate.HasValue)
        {
            whereConditions.Add("i.InspectedAt >= @StartDate");
            parameters.Add("StartDate", filter.StartDate.Value.ToString("O"));
        }

        if (filter.EndDate.HasValue)
        {
            whereConditions.Add("i.InspectedAt <= @EndDate");
            parameters.Add("EndDate", filter.EndDate.Value.ToString("O"));
        }

        // 結果篩選
        if (!string.IsNullOrWhiteSpace(filter.Result))
        {
            whereConditions.Add("i.Result = @Result");
            parameters.Add("Result", filter.Result);
        }

        // 瑕疵類型篩選
        if (!string.IsNullOrWhiteSpace(filter.DefectType))
        {
            whereConditions.Add("EXISTS (SELECT 1 FROM Defects d WHERE d.InspectionId = i.Id AND d.DefectType = @DefectType)");
            parameters.Add("DefectType", filter.DefectType);
        }

        var whereClause = whereConditions.Count > 0
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";

        // 查詢總數
        var countSql = $@"
            SELECT COUNT(*)
            FROM Inspections i
            INNER JOIN WorkOrders wo ON i.WorkOrderId = wo.Id
            {whereClause};
        ";

        // 查詢數據
        var dataSql = $@"
            SELECT
                i.Id,
                wo.Code AS WorkOrderCode,
                i.InspectedAt,
                i.Result,
                i.Confidence,
                i.ImagePath,
                i.AnnotatedImagePath,
                (SELECT COUNT(*) FROM Defects WHERE InspectionId = i.Id) AS DefectCount
            FROM Inspections i
            INNER JOIN WorkOrders wo ON i.WorkOrderId = wo.Id
            {whereClause}
            ORDER BY i.InspectedAt DESC
            LIMIT @PageSize OFFSET @Offset;
        ";

        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", pageIndex * pageSize);

        using var connection = _connectionFactory.CreateConnection();

        var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);
        var items = await connection.QueryAsync<InspectionHistoryRow>(dataSql, parameters);

        return new PagedResult<InspectionHistoryDto>
        {
            Items = items.Select(r => r.ToDto()).ToList(),
            TotalCount = totalCount,
            PageIndex = pageIndex,
            PageSize = pageSize
        };
    }

    public async Task<InspectionDetailDto?> GetDetailAsync(Guid inspectionId, CancellationToken cancellationToken)
    {
        const string inspectionSql = @"
            SELECT
                i.Id,
                wo.Code AS WorkOrderCode,
                wo.ProductName,
                wo.ModelName,
                i.InspectedAt,
                i.Result,
                i.Confidence,
                i.ImagePath,
                i.AnnotatedImagePath,
                i.InferenceTimeMs
            FROM Inspections i
            INNER JOIN WorkOrders wo ON i.WorkOrderId = wo.Id
            WHERE i.Id = @InspectionId;
        ";

        const string defectsSql = @"
            SELECT
                DefectType AS Type,
                Confidence,
                BoundingBoxX AS X,
                BoundingBoxY AS Y,
                BoundingBoxWidth AS Width,
                BoundingBoxHeight AS Height,
                Severity
            FROM Defects
            WHERE InspectionId = @InspectionId;
        ";

        using var connection = _connectionFactory.CreateConnection();

        var inspection = await connection.QuerySingleOrDefaultAsync<InspectionDetailRow>(
            inspectionSql,
            new { InspectionId = inspectionId.ToString() });

        if (inspection == null)
        {
            return null;
        }

        var defects = await connection.QueryAsync<DefectDetailDto>(
            defectsSql,
            new { InspectionId = inspectionId.ToString() });

        return inspection.ToDto(defects.ToList());
    }

    public async Task<IReadOnlyList<string>> GetWorkOrderCodesAsync(CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT DISTINCT Code
            FROM WorkOrders
            ORDER BY StartAt DESC
            LIMIT 100;
        ";

        using var connection = _connectionFactory.CreateConnection();
        var codes = await connection.QueryAsync<string>(sql);

        return codes.ToList();
    }

    // 輔助類：歷史記錄行映射
    private sealed class InspectionHistoryRow
    {
        public string Id { get; set; } = string.Empty;
        public string WorkOrderCode { get; set; } = string.Empty;
        public string InspectedAt { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public float? Confidence { get; set; }
        public string? ImagePath { get; set; }
        public string? AnnotatedImagePath { get; set; }
        public int DefectCount { get; set; }

        public InspectionHistoryDto ToDto()
        {
            return new InspectionHistoryDto
            {
                Id = Guid.Parse(Id),
                WorkOrderCode = WorkOrderCode,
                InspectedAt = DateTime.Parse(InspectedAt),
                Result = Result,
                Confidence = Confidence,
                ImagePath = ImagePath,
                AnnotatedImagePath = AnnotatedImagePath,
                DefectCount = DefectCount
            };
        }
    }

    // 輔助類：詳細記錄行映射
    private sealed class InspectionDetailRow
    {
        public string Id { get; set; } = string.Empty;
        public string WorkOrderCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ModelName { get; set; }
        public string InspectedAt { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public float? Confidence { get; set; }
        public string? ImagePath { get; set; }
        public string? AnnotatedImagePath { get; set; }
        public int? InferenceTimeMs { get; set; }

        public InspectionDetailDto ToDto(IReadOnlyList<DefectDetailDto> defects)
        {
            return new InspectionDetailDto
            {
                Id = Guid.Parse(Id),
                WorkOrderCode = WorkOrderCode,
                ProductName = ProductName,
                ModelName = ModelName,
                InspectedAt = DateTime.Parse(InspectedAt),
                Result = Result,
                Confidence = Confidence,
                ImagePath = ImagePath,
                AnnotatedImagePath = AnnotatedImagePath,
                InferenceTimeMs = InferenceTimeMs,
                Defects = defects
            };
        }
    }
}
