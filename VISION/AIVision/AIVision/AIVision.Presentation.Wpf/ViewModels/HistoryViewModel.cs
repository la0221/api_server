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

    [ObservableProperty]
    private string? selectedWorkOrderCode;

    [ObservableProperty]
    private DateTime? filterStartDate;

    [ObservableProperty]
    private DateTime? filterEndDate;

    /// <summary>
    /// 結果篩選 — 三態 Outcome 值(Match / TrustInput / MixedAlarm / Skip),null = 全部。
    /// </summary>
    [ObservableProperty]
    private string? filterResult;

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
                Result = FilterResult  // 三態 Outcome 值
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
        ExpectedCode = dto.ExpectedCode;
        ReadCode = dto.ReadCode;
        Outcome = string.IsNullOrEmpty(dto.Outcome) ? dto.Result : dto.Outcome;
        AirBlown = dto.AirBlown;

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

    // ===== 模號三態核對欄位 =====
    public string? ExpectedCode { get; }
    public string? ReadCode { get; }
    public string Outcome { get; }
    public bool AirBlown { get; }

    public string Timestamp => InspectedAt.ToString("yyyy/MM/dd HH:mm:ss");
    public string ConfidenceText => Confidence.HasValue ? $"{Confidence.Value:P1}" : "-";

    /// <summary>預期模號顯示(空 → "-")。</summary>
    public string ExpectedCodeText => string.IsNullOrEmpty(ExpectedCode) ? "-" : ExpectedCode!;

    /// <summary>讀到模號顯示(空 → "-")。</summary>
    public string ReadCodeText => string.IsNullOrEmpty(ReadCode) ? "-" : ReadCode!;

    /// <summary>
    /// 判斷是否為「不良/混料」(以三態 Outcome 為準):MixedAlarm = NG。
    /// </summary>
    public bool IsNg => Outcome.Equals("MixedAlarm", StringComparison.OrdinalIgnoreCase);

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
