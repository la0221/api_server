using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Contracts.ProductionStats;
using AIVision.Application.Ports.ProductionStats;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.ProductionStats;
using AIVision.Presentation.Wpf.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class ProductionStatsViewModel : ObservableObject
{
    private readonly IProductionStatsQuery _query;
    private readonly IProductionStatsConfigProvider _configProvider;
    private readonly IProductionStatsExportService _exportService;
    private readonly ModelConfigService _modelConfigService;

    public ProductionStatsViewModel(
        IProductionStatsQuery query,
        IProductionStatsConfigProvider configProvider,
        IProductionStatsExportService exportService,
        ModelConfigService modelConfigService)
    {
        _query = query;
        _configProvider = configProvider;
        _exportService = exportService;
        _modelConfigService = modelConfigService;
        _configProvider.ConfigurationChanged += OnConfigurationChanged;

        FilterStart = DateTime.Today.AddDays(-7);
        FilterEnd = DateTime.Today.AddDays(1);
    }

    public ObservableCollection<WorkOrderSummaryDto> Orders { get; } = new();
    public ObservableCollection<WorkOrderSummaryDto> SelectedOrders { get; } = new();

    [ObservableProperty]
    private WorkOrderSummaryDto? selectedOrder;

    public ObservableCollection<SummaryFieldViewModel> SummaryFields { get; } = new();

    public ObservableCollection<DefectRowViewModel> DefectRows { get; } = new();

    [ObservableProperty]
    private DateTime? filterStart;

    [ObservableProperty]
    private DateTime? filterEnd;

    [ObservableProperty]
    private string? filterProduct;

    [ObservableProperty]
    private string? filterOrder;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    partial void OnSelectedOrderChanged(WorkOrderSummaryDto? oldValue, WorkOrderSummaryDto? newValue)
    {
        _ = RefreshSummaryAsync();
    }

    private void OnConfigurationChanged(object? sender, ProductionStatsUiConfig e)
    {
        _ = RefreshSummaryAsync();
    }

    [RelayCommand]
    private async Task QueryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "查詢中...";

            var items = await _query.FindOrdersAsync(FilterStart, FilterEnd, FilterProduct, FilterOrder, CancellationToken.None);

            Orders.Clear();
            SelectedOrders.Clear();
            foreach (var item in items)
            {
                Orders.Add(item);
            }

            SelectedOrder = Orders.FirstOrDefault();
            StatusMessage = $"查詢完成，共 {Orders.Count} 筆工單。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"查詢失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void UpdateSelection(IEnumerable<WorkOrderSummaryDto> selection)
    {
        if (selection is null)
        {
            return;
        }

        SelectedOrders.Clear();
        foreach (var item in selection)
        {
            SelectedOrders.Add(item);
        }

        var next = SelectedOrders.LastOrDefault();
        if (!EqualityComparer<WorkOrderSummaryDto?>.Default.Equals(SelectedOrder, next))
        {
            SelectedOrder = next;
        }
        else if (next is not null)
        {
            _ = RefreshSummaryAsync();
        }
    }

    [RelayCommand]
    private Task ExportCsvAsync() =>
        ExportAsync((stats, config, token) => _exportService.ExportCsvAsync(stats, config, token), "CSV 匯出中...", "CSV 匯出完成");

    [RelayCommand]
    private Task ExportExcelAsync() =>
        ExportAsync((stats, config, token) => _exportService.ExportExcelAsync(stats, config, token), "Excel 匯出中...", "Excel 匯出完成");

    private async Task RefreshSummaryAsync()
    {
        SummaryFields.Clear();
        DefectRows.Clear();

        if (SelectedOrder is null)
        {
            return;
        }

        try
        {
            var stats = await _query.GetStatsAsync(SelectedOrder.Id, CancellationToken.None);
            if (stats is null)
            {
                return;
            }

            var config = _configProvider.Current;

            foreach (var field in config.SummaryFields)
            {
                var value = ObjectPathResolver.Resolve(stats, field.Path) ?? ObjectPathResolver.Resolve(stats.Order, field.Path);
                var formatted = ObjectPathResolver.Format(value, field.Format);
                SummaryFields.Add(new SummaryFieldViewModel(field.Label, formatted, field.LabelWidth, field.ValueWidth));
            }

            var total = stats.Total > 0 ? stats.Total : 1;

            // 模號核對結果分布：固定四態(Match / TrustInput / MixedAlarm / Skip)。
            foreach (var (key, label) in OutcomeRows)
            {
                stats.Defects.TryGetValue(key, out var count);
                DefectRows.Add(new DefectRowViewModel(label, count, (double)count / total));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入統計失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 模號核對結果分布固定四態(鍵 = MarkingVerifyOutcome 名稱;標籤為中英對照)。
    /// </summary>
    private static readonly (string Key, string Label)[] OutcomeRows =
    {
        ("Match", "相符 (Match)"),
        ("TrustInput", "採信輸入 (TrustInput)"),
        ("MixedAlarm", "混料警報 (MixedAlarm)"),
        ("Skip", "略過 (Skip)")
    };

    private async Task ExportAsync(
        Func<IEnumerable<WorkOrderStatsDto>, ProductionStatsUiConfig, CancellationToken, Task<string>> exporter,
        string inProgressMessage,
        string successPrefix)
    {
        if (IsBusy)
        {
            return;
        }

        var targets = SelectedOrders.Count > 0
            ? SelectedOrders.ToList()
            : SelectedOrder is not null
                ? new List<WorkOrderSummaryDto> { SelectedOrder }
                : new List<WorkOrderSummaryDto>();

        if (targets.Count == 0)
        {
            StatusMessage = "請先選擇工單";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = inProgressMessage;

            var statsList = new List<WorkOrderStatsDto>();
            foreach (var summary in targets)
            {
                var stats = await _query.GetStatsAsync(summary.Id, CancellationToken.None);
                if (stats is not null)
                {
                    statsList.Add(stats);
                }
            }

            if (statsList.Count == 0)
            {
                StatusMessage = "查無統計資料";
                return;
            }

            var path = await exporter(statsList, _configProvider.Current, CancellationToken.None);
            StatusMessage = $"{successPrefix}：{path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"匯出失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
