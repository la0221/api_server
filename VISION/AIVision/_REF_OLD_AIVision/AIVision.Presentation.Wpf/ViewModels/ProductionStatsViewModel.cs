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

            // 動態獲取瑕疵類型：優先從工單關聯的模型取得
            var defectTypes = await GetDefectTypesForOrderAsync(SelectedOrder.ModelName);

            foreach (var (typeName, displayName) in defectTypes)
            {
                stats.Defects.TryGetValue(typeName, out var count);
                DefectRows.Add(new DefectRowViewModel(displayName, count, (double)count / total));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入統計失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 根據模型名稱獲取瑕疵類型列表。
    /// </summary>
    private async Task<IReadOnlyList<(string TypeName, string DisplayName)>> GetDefectTypesForOrderAsync(string? modelName)
    {
        try
        {
            ModelConfig? model = null;

            // 嘗試從模型名稱獲取配置
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                model = await _modelConfigService.GetModelByNameAsync(modelName);
            }

            // 如果沒有找到模型配置，使用當前模型
            model ??= await _modelConfigService.GetCurrentModelAsync();

            if (model != null)
            {
                var resultTypes = model.GetEffectiveResultTypes();
                return resultTypes
                    .Select(t => (t, model.GetDisplayName(t)))
                    .ToList();
            }
        }
        catch
        {
            // 忽略錯誤，使用後備值
        }

        // 後備：從配置檔案獲取
        var config = _configProvider.Current;
        return config.DefectBuckets
            .Select(b => (b, config.DefectLabelMap.TryGetValue(b, out var lbl) ? lbl : b))
            .ToList();
    }

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
