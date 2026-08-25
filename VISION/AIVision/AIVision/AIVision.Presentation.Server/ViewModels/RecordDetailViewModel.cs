using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AIVision.Infrastructure.MoldCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Server.ViewModels;

/// <summary>
/// 單筆收件的詳細頁（站點細節頁再點進去看到的東西）。
///
/// <para>一樣走條列式：把這一筆的**每個事實**列清楚——誰送的、讀到什麼、
/// 收到多大、前處理在哪做、哪個模型版本、耗時、原圖在站端哪、父端有沒有留檔、留在哪。</para>
///
/// <para>影像只有在父端**開了留存**時才看得到；沒開就明講原因，不要留一個空白框讓人猜。</para>
/// </summary>
public partial class RecordDetailViewModel : ObservableObject
{
    private readonly InferenceMonitorClient _monitor;
    private readonly ILogger? _logger;
    private readonly long _seq;

    public RecordDetailViewModel(long seq, InferenceMonitorClient monitor, ILogger? logger = null)
    {
        _seq = seq;
        _monitor = monitor;
        _logger = logger;
        Title = $"收件 #{seq}";
    }

    /// <summary>條列式欄位（標籤 → 值）。</summary>
    public ObservableCollection<DetailField> Fields { get; } = new();

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _reading = "-";
    [ObservableProperty] private string _statusText = "-";
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _needsReview;
    [ObservableProperty] private BitmapSource? _image;
    [ObservableProperty] private string _imageNote = string.Empty;
    [ObservableProperty] private bool _hasImage;

    [RelayCommand]
    public async Task LoadAsync()
    {
        var item = await _monitor.GetRecentOneAsync(_seq).ConfigureAwait(true);
        if (item is null)
        {
            StatusText = "找不到這筆紀錄";
            ImageNote = "可能已被較新的紀錄擠出保留範圍，或推論服務重啟過（父端紀錄只在記憶體）。";
            return;
        }

        Reading = string.IsNullOrWhiteSpace(item.Reading) ? "-" : item.Reading!;
        IsOk = item.Ok && item.HasReading;
        NeedsReview = item.NeedsReview;
        StatusText = !item.Ok
            ? "推論失敗"
            : item.HasReading ? (item.NeedsReview ? "已讀出（建議人工複檢）" : "已讀出") : "未讀出";

        Fields.Clear();
        Add("流水號", $"#{item.Seq}");
        Add("時間", item.Timestamp == default ? (item.Time ?? "-") : item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        Add("來源站", item.StationId ?? "-");
        Add("用途", item.Task ?? "-");
        Add("讀值", Reading);
        Add("判定", StatusText);
        Add("收到大小", $"{item.ReceivedBytes / 1000.0:F1} KB（{item.ReceivedBytes:N0} bytes）");
        Add("前處理在哪做", item.IsStrip
            ? "站端（父端只做辨識，不再找圓／展開）"
            : "父端（站端送的是原圖）");
        Add("模型版本", string.IsNullOrWhiteSpace(item.ModelVersion) ? "-" : item.ModelVersion!);
        Add("父端整段耗時", $"{item.ElapsedMs} ms");
        Add("推論引擎耗時", $"{item.EngineMs} ms");
        Add("站端原圖位置", string.IsNullOrWhiteSpace(item.EdgeRawPath)
            ? "（站端未提供——舊版站端不會帶這個欄位）"
            : item.EdgeRawPath!);
        Add("父端留存位置", string.IsNullOrWhiteSpace(item.SavedImagePath)
            ? "（未留存）"
            : item.SavedImagePath!);
        if (!string.IsNullOrWhiteSpace(item.Error))
            Add("失敗原因", item.Error!);

        HasImage = item.HasImage;
        if (!item.HasImage)
        {
            ImageNote = "父端沒有留存這張圖。要看實體影像，請到站點細節頁把「留存收到的圖」打開，" +
                        "之後送進來的圖才會留檔（既有紀錄不會回溯）。";
            return;
        }

        var bytes = await _monitor.GetRecentImageAsync(_seq).ConfigureAwait(true);
        if (bytes is null || bytes.Length == 0)
        {
            ImageNote = "留存檔案讀不到（可能已超過保留張數被清掉，或檔案被移走）。";
            HasImage = false;
            return;
        }

        Image = ToBitmap(bytes);
        ImageNote = Image is null
            ? "影像解碼失敗。"
            : $"這是**父端實際收到**的影像（{bytes.Length / 1000.0:F1} KB）。";
    }

    private void Add(string label, string value) => Fields.Add(new DetailField(label, value));

    private BitmapSource? ToBitmap(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[RecordDetail] 影像解碼失敗 seq={Seq}", _seq);
            return null;
        }
    }
}

/// <summary>條列式的一列。</summary>
public sealed record DetailField(string Label, string Value);
