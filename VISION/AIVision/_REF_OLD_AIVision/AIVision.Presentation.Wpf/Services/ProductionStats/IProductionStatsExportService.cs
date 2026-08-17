using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Contracts.ProductionStats;

namespace AIVision.Presentation.Wpf.Services.ProductionStats;

public interface IProductionStatsExportService
{
    Task<string> ExportCsvAsync(IEnumerable<WorkOrderStatsDto> stats, ProductionStatsUiConfig config, CancellationToken cancellationToken);

    Task<string> ExportExcelAsync(IEnumerable<WorkOrderStatsDto> stats, ProductionStatsUiConfig config, CancellationToken cancellationToken);
}
