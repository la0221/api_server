using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AIVision.Application.Ports.History;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IInspectionHistoryQuery _query;
    private readonly ModelConfigService _modelConfigService;
    private const int PageSize = 50;

    public HistoryViewModel(IInspectionHistoryQuery query, ModelConfigService modelConfigService)
    {
        _query = query;
        _modelConfigService = modelConfigService;

        // 初始化篩選條件
        FilterStartDate = DateTime.Today.AddDays(-7);
        FilterEndDate = DateTime.Today.AddDays(1);

        // 載入工單列表和歷史記錄
        _ = LoadAsync();
    }

    public ObservableCollection<InspectionHistoryItemViewModel> HistoryItems { get; } = new();

    public ObservableCollection<string> WorkOrderCodes { get; } = new();

    /// <summary>
    /// 瑕疵類型篩選選項（動態從模型配置載入）
    /// </summary>
    public ObservableCollection<DefectTypeFilterOption> DefectTypeOptions { get; } = new();

    [ObservableProperty]
    private string? selectedWorkOrderCode;

    [ObservableProperty]
    private DateTime? filterStartDate;

    [ObservableProperty]
    private DateTime? filterEndDate;

    [ObservableProperty]
    private string? filterResult;  // "OK", "NG", or null

    [ObservableProperty]
    private DefectTypeFilterOption? selectedDefectTypeOption;

    /// <summary>
    /// 篩選用的瑕疵類型值（從選中選項取得）
    /// </summary>
    public string? FilterDefectType => SelectedDefectTypeOption?.Value;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private int currentPageIndex;

    [ObservableProperty]
    private int totalPages;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    private InspectionHistoryItemViewModel? selectedItem;

    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "載入中...";

            // 載入工單列表
            var codes = await _query.GetWorkOrderCodesAsync(CancellationToken.None);
            WorkOrderCodes.Clear();
            WorkOrderCodes.Add("全部");  // 添加"全部"選項
            foreach (var code in codes)
            {
                WorkOrderCodes.Add(code);
            }

            // 預設選擇"全部"
            SelectedWorkOrderCode = "全部";

            // 載入瑕疵類型篩選選項（從所有模型配置彙總）
            await LoadDefectTypeOptionsAsync();

            // 查詢歷史記錄
            await QueryAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 從所有模型配置載入瑕疵類型篩選選項
    /// </summary>
    private async Task LoadDefectTypeOptionsAsync()
    {
        try
        {
            DefectTypeOptions.Clear();
            DefectTypeOptions.Add(new DefectTypeFilterOption(null, "全部"));

            var config = await _modelConfigService.LoadAsync();
            var allTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 彙總所有模型的結果類型
            foreach (var model in config.Models)
            {
                foreach (var typeName in model.GetEffectiveResultTypes())
                {
                    if (!allTypes.ContainsKey(typeName))
                    {
                        allTypes[typeName] = model.GetDisplayName(typeName);
                    }
                }
            }

            // 添加到選項列表
            foreach (var (typeName, displayName) in allTypes)
            {
                DefectTypeOptions.Add(new DefectTypeFilterOption(typeName, displayName));
            }
        }
        catch
        {
            // 忽略錯誤，使用預設空選項
        }
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
            CurrentPageIndex = 0;

            await LoadPageAsync();
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

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPageIndex > 0)
        {
            CurrentPageIndex--;
            await LoadPageAsync();
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPageIndex < TotalPages - 1)
        {
            CurrentPageIndex++;
            await LoadPageAsync();
        }
    }

    private async Task LoadPageAsync()
    {
        try
        {
            var filter = new InspectionQueryFilter
            {
                WorkOrderCode = SelectedWorkOrderCode == "全部" ? null : SelectedWorkOrderCode,
                StartDate = FilterStartDate,
                EndDate = FilterEndDate,
                Result = FilterResult,
                DefectType = FilterDefectType
            };

            var result = await _query.QueryAsync(filter, CurrentPageIndex, PageSize, CancellationToken.None);

            HistoryItems.Clear();
            foreach (var item in result.Items)
            {
                HistoryItems.Add(new InspectionHistoryItemViewModel(item));
            }

            TotalCount = result.TotalCount;
            TotalPages = result.TotalPages;
            StatusMessage = $"查詢完成，共 {TotalCount} 筆記錄。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入失敗：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ViewDetail(InspectionHistoryItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        // 找到當前項目在列表中的索引
        var index = HistoryItems.IndexOf(item);
        if (index < 0)
        {
            index = 0;
        }

        // 開啟圖片放大視窗
        var viewer = new Views.ImageViewerWindow(HistoryItems, index);
        viewer.ShowDialog();
    }

    [RelayCommand]
    private void ExportSelected()
    {
        // 批量導出功能（未來實作）
        StatusMessage = "導出功能開發中...";
    }
}

/// <summary>
/// 歷史記錄項目 ViewModel
/// </summary>
public sealed partial class InspectionHistoryItemViewModel : ObservableObject
{
    public InspectionHistoryItemViewModel(InspectionHistoryDto dto)
    {
        Id = dto.Id;
        WorkOrderCode = dto.WorkOrderCode;
        InspectedAt = dto.InspectedAt;
        Result = dto.Result;
        Confidence = dto.Confidence;
        ImagePath = dto.ImagePath;
        AnnotatedImagePath = dto.AnnotatedImagePath;
        DefectCount = dto.DefectCount;

        // 載入縮略圖
        LoadThumbnail();
    }

    public Guid Id { get; }
    public string WorkOrderCode { get; }
    public DateTime InspectedAt { get; }
    public string Result { get; }
    public float? Confidence { get; }
    public string? ImagePath { get; }
    public string? AnnotatedImagePath { get; }
    public int DefectCount { get; }

    public string Timestamp => InspectedAt.ToString("yyyy/MM/dd HH:mm:ss");
    public string ConfidenceText => Confidence.HasValue ? $"{Confidence.Value:P1}" : "-";

    /// <summary>
    /// 判斷是否為 NG
    /// - DefectCount > 0 時為 NG
    /// - Result 不是 "OK" 且不為空時為 NG（代表存的是瑕疵名稱，如 "刮痕"、"TF碰撞" 等）
    /// </summary>
    public bool IsNg => DefectCount > 0 ||
        (!Result.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrEmpty(Result));

    [ObservableProperty]
    private BitmapSource? thumbnail;

    [ObservableProperty]
    private InspectionDetailDto? detail;

    private void LoadThumbnail()
    {
        Task.Run(() =>
        {
            try
            {
                // NG 優先顯示標註圖，否則顯示原圖
                var imagePath = IsNg && !string.IsNullOrEmpty(AnnotatedImagePath)
                    ? AnnotatedImagePath
                    : ImagePath;

                if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 200;  // 縮略圖寬度
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        Thumbnail = bitmap;
                    });
                }
            }
            catch
            {
                // 載入失敗，忽略
            }
        });
    }

    public void LoadDetail(InspectionDetailDto detailDto)
    {
        Detail = detailDto;
    }
}

/// <summary>
/// 瑕疵類型篩選選項
/// </summary>
public sealed class DefectTypeFilterOption
{
    public DefectTypeFilterOption(string? value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    /// <summary>
    /// 瑕疵類型值（null 表示「全部」）
    /// </summary>
    public string? Value { get; }

    /// <summary>
    /// 顯示名稱
    /// </summary>
    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}
