using AIVision.Application.Contracts.ProductionStats;
using AIVision.Application.Ports.ProductionStats;

namespace AIVision.Presentation.Wpf.Adapters.ProductionStats;

public sealed class FakeProductionStatsQuery : IProductionStatsQuery
{
    private readonly List<WorkOrderSummaryDto> _orders;
    private readonly Dictionary<Guid, WorkOrderStatsDto> _stats;

    public FakeProductionStatsQuery()
    {
        _orders = new List<WorkOrderSummaryDto>();
        _stats = new Dictionary<Guid, WorkOrderStatsDto>();

        var random = new Random(123);
        var defects = new[] { "misalign", "residual", "short", "bridging", "winding" };

        for (int i = 0; i < 15; i++)
        {
            var id = Guid.NewGuid();
            var start = DateTime.Today.AddDays(-i).AddHours(random.Next(0, 8));
            var end = start.AddMinutes(random.Next(45, 180));
            var code = $"WO-{DateTime.Today.AddDays(-i):yyyyMMdd}-{i:00}";
            var product = $"Line-{random.Next(1, 4)}";

            var summary = new WorkOrderSummaryDto(id, code, product, start, end);
            _orders.Add(summary);

            var total = random.Next(80, 150);
            var ng = random.Next(0, 15);
            var ok = total - ng;
            var defectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var defect in defects)
            {
                defectCounts[defect] = random.Next(0, Math.Max(1, ng / defects.Length));
            }

            var stats = new WorkOrderStatsDto
            {
                Order = summary,
                Total = total,
                Ok = ok,
                Ng = ng,
                Defects = defectCounts
            };

            _stats[id] = stats;
        }
    }

    public Task<IReadOnlyList<WorkOrderSummaryDto>> FindOrdersAsync(DateTime? start, DateTime? end, string? product, string? orderKeyword, CancellationToken cancellationToken)
    {
        IEnumerable<WorkOrderSummaryDto> query = _orders;

        if (start.HasValue)
        {
            query = query.Where(o => o.StartAt >= start.Value);
        }

        if (end.HasValue)
        {
            query = query.Where(o => (o.EndAt ?? DateTime.MaxValue) <= end.Value);
        }

        if (!string.IsNullOrWhiteSpace(product))
        {
            query = query.Where(o => o.Product.Contains(product, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(orderKeyword))
        {
            query = query.Where(o => o.Code.Contains(orderKeyword, StringComparison.OrdinalIgnoreCase));
        }

        var list = query
            .OrderByDescending(o => o.StartAt)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<WorkOrderSummaryDto>>(list);
    }

    public Task<WorkOrderStatsDto?> GetStatsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        _stats.TryGetValue(orderId, out var value);
        return Task.FromResult<WorkOrderStatsDto?>(value);
    }
}
