using AIVision.Application.Configuration;

namespace AIVision.Application.Ports.ProductionStats;

public interface IProductionStatsConfigProvider
{
    ProductionStatsUiConfig Current { get; }

    event EventHandler<ProductionStatsUiConfig>? ConfigurationChanged;
}
