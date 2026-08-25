using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIVision.Infrastructure.MoldCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Server.ViewModels;

/// <summary>
/// 站點細節頁（模號穴號／公母模／瑕疵檢查點進去看到的東西）。
///
/// <para>版面刻意走**條列式**：現場要的是「這個站點現在什麼狀況」的一串事實，
/// 不是一堆並排的儀表。想看更細再點單筆進去（<see cref="RecordDetailViewModel"/>）。</para>
///
/// <list type="bullet">
/// <item><b>這個站點在做什麼</b>、能不能推論</item>
/// <item><b>引擎與模型</b>：每個引擎的現用版本／可選版本／已載入／登錄夾路徑／檔案組成，可直接切版</item>
/// <item><b>收到的圖</b>：要不要留存（可即時開關）、**存放點**、已存幾張多大</item>
/// <item><b>最近辨識</b>：只列這個站點的，點單筆看詳細</item>
/// </list>
/// </summary>
public partial class StationDetailViewModel : ObservableObject, IDisposable
{
    private readonly InferenceMonitorClient _monitor;
    private readonly ILogger? _logger;
    private readonly DispatcherTimer _timer;
    private readonly Func<RecentInferenceRow, Task> _openRecord;

    public StationDetailViewModel(
        string groupKey,
        string groupName,
        InferenceMonitorClient monitor,
        Func<RecentInferenceRow, Task> openRecord,
        ILogger? logger = null)
    {
        GroupKey = groupKey;
        GroupName = groupName;
        _monitor = monitor;
        _openRecord = openRecord;
        _logger = logger;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public string GroupKey { get; }

    [ObservableProperty] private string _groupName = string.Empty;
    [ObservableProperty] private string _purposeText = string.Empty;
    [ObservableProperty] private bool _inferReady;
    [ObservableProperty] private string _readyText = "-";
    [ObservableProperty] private string _actionMessage = string.Empty;
    [ObservableProperty] private string _lastCheckedText = "-";

    // ── 收到的圖 ─────────────────────────────
    [ObservableProperty] private bool _saveImages;
    [ObservableProperty] private string _imageFolder = "-";
    [ObservableProperty] private string _imageStatText = "-";
    /// <summary>避免「程式更新勾選狀態」被誤當成使用者按下去而反覆打 API。</summary>
    private bool _suppressSaveToggle;

    /// <summary>這個站點底下的引擎（可切版）。</summary>
    public ObservableCollection<ModelPoolRow> Engines { get; } = new();

    /// <summary>只屬於這個站點的最近辨識紀錄。</summary>
    public ObservableCollection<RecentInferenceRow> RecentItems { get; } = new();

    /// <summary>這個站點涵蓋的 task（用來從全域紀錄裡篩自己的）。</summary>
    private readonly HashSet<string> _tasks = new(StringComparer.OrdinalIgnoreCase);

    partial void OnSaveImagesChanged(bool value)
    {
        if (_suppressSaveToggle) return;
        _ = ApplySaveImagesAsync(value);
    }

    private async Task ApplySaveImagesAsync(bool save)
    {
        var info = await _monitor.SetImageSaveAsync(save).ConfigureAwait(true);
        if (info is null)
        {
            ActionMessage = "⚠ 切換「留存收到的圖」失敗——推論服務沒回應。";
            return;
        }
        ApplyImageInfo(info);
        ActionMessage = save
            ? $"✔ 已開始留存收到的圖 → {info.Folder}（⚠ 只影響本次執行；永久生效請改 appsettings 的 ReceivedImages:Save）"
            : "✔ 已停止留存收到的圖（既有檔案不會被刪）。";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var pools = await _monitor.GetPoolsAsync().ConfigureAwait(true);
            var mine = pools?.Pools?
                .Where(p => string.Equals(p.GroupKey, GroupKey, StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<ModelPoolItem>();

            if (mine.Count > 0)
            {
                _tasks.Clear();
                foreach (var p in mine)
                    if (!string.IsNullOrWhiteSpace(p.Task)) _tasks.Add(p.Task!);

                // 引擎數／版本清單沒變就只更新會動的欄位（別洗掉下拉選擇）
                var sameShape = Engines.Count == mine.Count &&
                                Engines.Zip(mine).All(x =>
                                    string.Equals(x.First.TaskKey, x.Second.Task, StringComparison.OrdinalIgnoreCase) &&
                                    x.First.Versions.SequenceEqual(x.Second.Versions ?? new List<string>()));
                if (!sameShape)
                {
                    Engines.Clear();
                    foreach (var p in mine) Engines.Add(ModelPoolRow.From(p, ApplyVersionAsync));
                }
                else
                {
                    foreach (var (row, p) in Engines.Zip(mine)) row.UpdateLive(p);
                }

                InferReady = mine.Any(p => p.InferReady);
                ReadyText = InferReady ? "可接收送檢" : "尚無推論端點";
                PurposeText = BuildPurpose(mine);
            }

            var recent = await _monitor.GetRecentAsync(200).ConfigureAwait(true);
            if (recent is not null)
                SyncRecent(recent);

            var images = await _monitor.GetImageSettingsAsync().ConfigureAwait(true);
            if (images is not null) ApplyImageInfo(images);

            LastCheckedText = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[StationDetail] 更新失敗 {Group}", GroupKey);
            ActionMessage = $"更新失敗：{ex.Message}";
        }
    }

    private void SyncRecent(RecentInferenceDto dto)
    {
        var mine = dto.Items
            .Where(i => _tasks.Count == 0 || (i.Task is not null && _tasks.Contains(i.Task)))
            .ToList();

        var newestSeq = mine.Count > 0 ? mine[0].Seq : 0;
        var currentSeq = RecentItems.Count > 0 ? RecentItems[0].Seq : 0;
        if (newestSeq == currentSeq && RecentItems.Count == mine.Count) return;

        RecentItems.Clear();
        foreach (var it in mine)
            RecentItems.Add(RecentInferenceRow.From(it, _openRecord));
    }

    private void ApplyImageInfo(ReceivedImageSettingsInfo info)
    {
        _suppressSaveToggle = true;
        SaveImages = info.Save;
        _suppressSaveToggle = false;

        ImageFolder = string.IsNullOrWhiteSpace(info.Folder) ? "-" : info.Folder!;
        ImageStatText = info.Save || info.SavedCount > 0
            ? $"已留存 {info.SavedCount} 張（{info.SavedBytes / 1024.0 / 1024.0:F1} MB），保留上限 {info.MaxFiles} 張"
            : "目前不留存（原圖本來就在站端；要看實體檔案再打開）";
    }

    private static string BuildPurpose(IReadOnlyList<ModelPoolItem> pools)
    {
        var first = pools[0];
        var engines = string.Join("、", pools.Select(p => $"{p.EngineName}（{p.Task}）"));
        return first.GroupKey?.ToLowerInvariant() switch
        {
            "moldcode" => $"辨識鏡片上的模號與穴號。可用引擎：{engines}——**同一個站點、換引擎不換站點**。",
            "gongmu" => $"判別公模／母模。引擎：{engines}。",
            "defect" => $"鏡片瑕疵檢查。引擎：{engines}。",
            _ => $"引擎：{engines}。",
        };
    }

    private async Task ApplyVersionAsync(ModelPoolRow row)
    {
        if (string.IsNullOrWhiteSpace(row.SelectedVersion))
        {
            ActionMessage = $"請先選擇 {row.EngineName} 要用的版本。";
            return;
        }
        if (row.TaskKey is null) return;

        row.IsApplying = true;
        ActionMessage = $"{row.EngineName}：切換到 {row.SelectedVersion}…";
        try
        {
            var err = await _monitor.SetCurrentVersionAsync(row.TaskKey, row.SelectedVersion!)
                .ConfigureAwait(true);
            if (err is not null)
            {
                ActionMessage = $"⚠ {row.EngineName} 切版失敗：{err}";
                return;
            }
            row.CurrentVersion = row.SelectedVersion!;
            ActionMessage = $"✔ {row.EngineName} 已切到 {row.SelectedVersion}。" +
                            "若該版本還沒載入，下一張送檢會冷啟（20–90 秒）屬正常。";
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            row.IsApplying = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
