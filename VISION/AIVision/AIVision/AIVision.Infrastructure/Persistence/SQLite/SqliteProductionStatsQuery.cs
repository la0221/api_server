using AIVision.Application.Contracts.ProductionStats;
using AIVision.Application.Ports.ProductionStats;
using Dapper;

namespace AIVision.Infrastructure.Persistence.SQLite;

public sealed class SqliteProductionStatsQuery : IProductionStatsQuery
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public SqliteProductionStatsQuery(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<WorkOrderSummaryDto>> FindOrdersAsync(
        DateTime? start,
        DateTime? end,
        string? product,
        string? orderKeyword,
        CancellationToken cancellationToken)
    {
        var whereConditions = new List<string>();
        var parameters = new DynamicParameters();

        // Filter by date range
        if (start.HasValue)
        {
            whereConditions.Add("StartAt >= @StartDate");
            parameters.Add("StartDate", start.Value.ToString("O"));
        }

        if (end.HasValue)
        {
            whereConditions.Add("COALESCE(EndAt, datetime('now')) <= @EndDate");
            parameters.Add("EndDate", end.Value.ToString("O"));
        }

        // Filter by product name
        if (!string.IsNullOrWhiteSpace(product))
        {
            whereConditions.Add("ProductName LIKE @Product");
            parameters.Add("Product", $"%{product}%");
        }

        // Filter by work order code
        if (!string.IsNullOrWhiteSpace(orderKeyword))
        {
            whereConditions.Add("Code LIKE @OrderKeyword");
            parameters.Add("OrderKeyword", $"%{orderKeyword}%");
        }

        var whereClause = whereConditions.Count > 0
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";

        var sql = $@"
            SELECT
                Id,
                Code,
                ProductName AS Product,
                StartAt,
                EndAt,
                ModelName
            FROM WorkOrders
            {whereClause}
            ORDER BY StartAt DESC;
        ";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<WorkOrderSummaryRow>(sql, parameters);

        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task<WorkOrderStatsDto?> GetStatsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Query work order basic info
        const string orderSql = @"
            SELECT Id, Code, ProductName AS Product, StartAt, EndAt, ModelName
            FROM WorkOrders
            WHERE Id = @OrderId;
        ";

        // Query inspection statistics — 模號三態語意
        // (Ok = Match + TrustInput;Ng = MixedAlarm;Total 排除 Skip)。
        // 須與 SqliteInspectionRepository.GetStatisticsByWorkOrderIdAsync 保持一致。
        const string statsSql = @"
            SELECT
                SUM(CASE WHEN Outcome IN ('Match','TrustInput','MixedAlarm') THEN 1 ELSE 0 END) AS Total,
                SUM(CASE WHEN Outcome IN ('Match','TrustInput') THEN 1 ELSE 0 END) AS Ok,
                SUM(CASE WHEN Outcome = 'MixedAlarm' THEN 1 ELSE 0 END) AS Ng
            FROM Inspections
            WHERE WorkOrderId = @OrderId;
        ";

        // Query outcome distribution (取代舊 Defects 分組) — 依三態 Outcome 分組。
        const string outcomeSql = @"
            SELECT
                Outcome AS OutcomeKey,
                COUNT(*) AS Count
            FROM Inspections
            WHERE WorkOrderId = @OrderId
              AND Outcome IS NOT NULL
              AND Outcome != ''
            GROUP BY Outcome;
        ";

        using var connection = _connectionFactory.CreateConnection();

        var orderRow = await connection.QuerySingleOrDefaultAsync<WorkOrderSummaryRow>(
            orderSql,
            new { OrderId = orderId.ToString() });

        if (orderRow == null)
        {
            return null;
        }

        var statsRow = await connection.QuerySingleAsync<StatsRow>(
            statsSql,
            new { OrderId = orderId.ToString() });

        // Get outcome distribution (Defects dictionary 現承載 Outcome->count)
        var outcomeRows = await connection.QueryAsync<OutcomeCountRow>(
            outcomeSql,
            new { OrderId = orderId.ToString() });

        var outcomeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in outcomeRows)
        {
            outcomeCounts[row.OutcomeKey] = row.Count;
        }

        return new WorkOrderStatsDto
        {
            Order = orderRow.ToDto(),
            Total = statsRow.Total,
            Ok = statsRow.Ok,
            Ng = statsRow.Ng,
            Defects = outcomeCounts
        };
    }

    // Helper class: WorkOrderSummaryRow
    private sealed class WorkOrderSummaryRow
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Product { get; set; } = string.Empty;
        public string StartAt { get; set; } = string.Empty;
        public string? EndAt { get; set; }
        public string? ModelName { get; set; }

        public WorkOrderSummaryDto ToDto()
        {
            return new WorkOrderSummaryDto(
                Guid.Parse(Id),
                Code,
                Product,
                DateTime.Parse(StartAt),
                EndAt != null ? DateTime.Parse(EndAt) : null,
                ModelName
            );
        }
    }

    // Helper class: StatsRow
    private sealed class StatsRow
    {
        public int Total { get; set; }
        public int Ok { get; set; }
        public int Ng { get; set; }
    }

    // Helper class: OutcomeCountRow (依三態 Outcome 分組)
    private sealed class OutcomeCountRow
    {
        public string OutcomeKey { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
