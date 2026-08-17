using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Contracts.WorkOrder;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Ports.ProductionStats;
using AIVision.Application.Services;
using AIVision.Domain.Entities;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class WorkOrderManagementViewModel : ObservableObject
{
    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IInspectionRepository _inspectionRepository;
    private readonly IProductionStatsQuery _productionStatsQuery;
    private readonly IWorkOrderManagementService _workOrderService;
    private readonly INavigationService _navigationService;
    private readonly IMessenger _messenger;
    private readonly ModelConfigService _modelConfigService;

    public WorkOrderManagementViewModel(
        IWorkOrderRepository workOrderRepository,
        IInspectionRepository inspectionRepository,
        IProductionStatsQuery productionStatsQuery,
        IWorkOrderManagementService workOrderService,
        INavigationService navigationService,
        IMessenger messenger,
        ModelConfigService modelConfigService)
    {
        _workOrderRepository = workOrderRepository;
        _inspectionRepository = inspectionRepository;
        _productionStatsQuery = productionStatsQuery;
        _workOrderService = workOrderService;
        _navigationService = navigationService;
        _messenger = messenger;
        _modelConfigService = modelConfigService;
    }

    public ObservableCollection<WorkOrderItemViewModel> WorkOrders { get; } = new();

    [ObservableProperty]
    private WorkOrderItemViewModel? selectedWorkOrder;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "準備就緒";

    [ObservableProperty]
    private string filterStatus = "全部";

    public async Task LoadAsync()
    {
        await LoadWorkOrdersAsync();
    }

    [RelayCommand]
    private async Task LoadWorkOrdersAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "載入工單中...";

            // 從數據庫獲取所有工單
            var workOrders = await _workOrderRepository.GetAllAsync(CancellationToken.None);

            // 篩選
            var filtered = FilterStatus switch
            {
                "進行中" => workOrders.Where(w => w.Status == "Active"),
                "已結束" => workOrders.Where(w => w.Status == "Completed" || w.Status == "Cancelled"),
                _ => workOrders
            };

            // 按開始時間降序排列
            var sorted = filtered.OrderByDescending(w => w.StartAt).ToList();

            WorkOrders.Clear();

            foreach (var workOrder in sorted)
            {
                // 獲取該工單的統計信息
                var stats = await GetWorkOrderStatsAsync(workOrder.Id);

                var item = new WorkOrderItemViewModel
                {
                    Id = workOrder.Id,
                    Code = workOrder.Code,
                    ProductName = workOrder.ProductName,
                    ModelName = workOrder.ModelName ?? "未指定",
                    Status = workOrder.Status,
                    StartAt = workOrder.StartAt,
                    EndAt = workOrder.EndAt,
                    TotalCount = stats.Total,
                    NgCount = stats.Ng,
                    YieldPercentage = stats.Yield
                };

                WorkOrders.Add(item);
            }

            // 標示 + 自動選取「目前工單」，讓使用者一眼看到、不必每次重選。
            var current = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (current != null)
            {
                var currentItem = WorkOrders.FirstOrDefault(w => w.Id == current.Id);
                if (currentItem != null)
                {
                    currentItem.IsCurrent = true;
                    SelectedWorkOrder = currentItem;
                }
            }

            StatusMessage = current != null
                ? $"載入完成，共 {WorkOrders.Count} 筆；目前工單：{current.Code}"
                : $"載入完成，共 {WorkOrders.Count} 筆（目前無使用中工單）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入失敗：{ex.Message}";
            MessageBox.Show($"載入工單失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SwitchToWorkOrderAsync()
    {
        if (SelectedWorkOrder == null)
        {
            MessageBox.Show("請先選擇工單", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 檢查工單模型與當前選擇的模型是否一致
        var currentModel = await _modelConfigService.GetCurrentModelAsync();
        var workOrderModel = SelectedWorkOrder.ModelName;

        if (currentModel != null &&
            !string.IsNullOrEmpty(workOrderModel) &&
            workOrderModel != "未指定" &&
            !string.Equals(currentModel.Name, workOrderModel, StringComparison.OrdinalIgnoreCase))
        {
            var modelMismatchResult = MessageBox.Show(
                $"警告：工單使用的模型「{workOrderModel}」與目前選擇的模型「{currentModel.Name}」不同。\n\n" +
                $"請先到「模型選擇」頁面切換到「{workOrderModel}」模型，以確保推論結果正確。\n\n" +
                $"是否仍要繼續切換工單？",
                "模型不一致",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (modelMismatchResult != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (SelectedWorkOrder.Status != "Active")
        {
            var result = MessageBox.Show(
                $"工單 {SelectedWorkOrder.Code} 已結束，是否要重新啟用？\n注意：這將結束當前進行中的工單。",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            IsBusy = true;
            StatusMessage = "切換工單中...";

            await _workOrderService.SwitchToWorkOrderAsync(SelectedWorkOrder.Code, CancellationToken.None);

            StatusMessage = $"已切換到工單：{SelectedWorkOrder.Code}";
            MessageBox.Show($"已切換到工單：{SelectedWorkOrder.Code}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

            // 發送工單變更訊息，通知 ShellViewModel 更新顯示
            _messenger.Send(new WorkOrderChangedMessage());

            // 重新載入以更新狀態
            await LoadWorkOrdersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"切換失敗：{ex.Message}";
            MessageBox.Show($"切換工單失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CreateNewWorkOrder()
    {
        // 打開創建工單對話框
        var result = _navigationService.ShowDialog<Views.WorkOrderInputView>();
        if (result == true)
        {
            // 重新載入工單列表
            _ = LoadWorkOrdersAsync();
        }
    }

    [RelayCommand]
    private async Task EditWorkOrderAsync()
    {
        if (SelectedWorkOrder == null)
        {
            MessageBox.Show("請先選擇要編輯的工單", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 取完整實體（清單項未帶預期碼/批次）→ 帶入編輯對話框。
        var wo = await _workOrderRepository.GetByIdAsync(SelectedWorkOrder.Id, CancellationToken.None);
        if (wo == null)
        {
            MessageBox.Show("找不到工單資料", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var result = _navigationService.ShowDialog<Views.WorkOrderInputView>(v =>
        {
            if (v.DataContext is WorkOrderInputViewModel ivm)
                ivm.LoadForEdit(wo);
        });

        if (result == true)
        {
            await LoadWorkOrdersAsync();
            StatusMessage = $"已更新工單：{wo.Code}";
        }
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        _ = LoadWorkOrdersAsync();
    }

    [RelayCommand]
    private async Task DeleteWorkOrderAsync()
    {
        if (SelectedWorkOrder == null)
        {
            MessageBox.Show("請先選擇要刪除的工單", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 保存要刪除的工單參考
        var workOrderToDelete = SelectedWorkOrder;

        // 確認刪除
        var result = MessageBox.Show(
            $"確定要刪除工單「{workOrderToDelete.Code}」嗎？\n\n" +
            $"警告：此操作將同時刪除該工單下所有的檢測記錄，且無法復原！",
            "確認刪除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        // 二次確認
        var confirmResult = MessageBox.Show(
            $"再次確認：刪除工單「{workOrderToDelete.Code}」後無法復原。\n\n是否確定刪除？",
            "最終確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "刪除工單中...";

            // 從資料庫刪除
            await _workOrderRepository.DeleteAsync(workOrderToDelete.Id, CancellationToken.None);

            // 立即從 UI 集合移除（不需等待重新載入）
            WorkOrders.Remove(workOrderToDelete);
            SelectedWorkOrder = null;

            StatusMessage = $"已刪除工單：{workOrderToDelete.Code}";
            MessageBox.Show($"已成功刪除工單：{workOrderToDelete.Code}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

            // 發送工單變更訊息
            _messenger.Send(new WorkOrderChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"刪除失敗：{ex.Message}";
            MessageBox.Show($"刪除工單失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<(int Total, int Ng, double Yield)> GetWorkOrderStatsAsync(Guid workOrderId)
    {
        try
        {
            var stats = await _productionStatsQuery.GetStatsAsync(workOrderId, CancellationToken.None);
            if (stats == null)
            {
                return (0, 0, 100.0);
            }

            var yield = stats.Total > 0 ? (double)stats.Ok / stats.Total * 100 : 100.0;
            return (stats.Total, stats.Ng, yield);
        }
        catch
        {
            return (0, 0, 100.0);
        }
    }
}

/// <summary>
/// 工單項目 ViewModel
/// </summary>
public partial class WorkOrderItemViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid id;

    [ObservableProperty]
    private string code = string.Empty;

    [ObservableProperty]
    private string productName = string.Empty;

    [ObservableProperty]
    private string modelName = string.Empty;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private DateTime startAt;

    [ObservableProperty]
    private DateTime? endAt;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    private int ngCount;

    [ObservableProperty]
    private double yieldPercentage;

    /// <summary>是否為目前使用中的工單（清單標示 + 自動選取用）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBadge))]
    private bool isCurrent;

    /// <summary>清單「目前」欄顯示：目前工單顯示 ★。</summary>
    public string CurrentBadge => IsCurrent ? "★ 目前" : string.Empty;

    public string StatusDisplay => Status switch
    {
        "Active" => "進行中",
        "Completed" => "已完成",
        "Cancelled" => "已取消",
        _ => Status
    };

    public string DurationDisplay
    {
        get
        {
            var end = EndAt ?? DateTime.Now;
            var duration = end - StartAt;
            return duration.TotalHours >= 1
                ? $"{duration.TotalHours:F1} 小時"
                : $"{duration.TotalMinutes:F0} 分鐘";
        }
    }
}
