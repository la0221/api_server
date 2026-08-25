using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AIVision.Infrastructure.MoldCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIVision.Presentation.Server.ViewModels;

/// <summary>
/// 父端首頁的一張**站點**卡（模號穴號／公母模／瑕疵檢查）。
///
/// <para><b>為什麼是站點不是用途</b>：模號穴號不管走 CRNN 還是雙 head，
/// **都是同一個模號穴號站點**，只是換引擎——不該在畫面上變成兩張並排的卡
/// （2026-08-19 使用者指正）。所以卡片以站點為單位，引擎收進卡片裡面。</para>
///
/// <para>首頁只放「一眼要知道的」；細節一律點進去看（<see cref="StationDetailViewModel"/>）。</para>
/// </summary>
public partial class StationRow : ObservableObject
{
    private Func<StationRow, Task>? _openDetail;

    /// <summary>站點代號（moldcode／gongmu／defect）。</summary>
    [ObservableProperty] private string _groupKey = string.Empty;

    /// <summary>站點名稱（模號穴號／公母模／瑕疵檢查）。</summary>
    [ObservableProperty] private string _groupName = string.Empty;

    /// <summary>現在實際在用的引擎＋版本，例如「CRNN 字元式 · b3」。</summary>
    [ObservableProperty] private string _currentText = "-";

    /// <summary>引擎摘要，例如「2 個引擎 · 8 個版本」。</summary>
    [ObservableProperty] private string _engineSummary = "-";

    /// <summary>這個站點累計收到幾筆（從最近紀錄篩出來的）。</summary>
    [ObservableProperty] private int _recentCount;

    /// <summary>最後一筆的時間與讀值，例如「14:27:51 M101/02」。</summary>
    [ObservableProperty] private string _lastText = "尚無紀錄";

    /// <summary>這個站點是否已有可用的推論端點。</summary>
    [ObservableProperty] private bool _inferReady;

    /// <summary>沒有推論端點時給人看的說明。</summary>
    [ObservableProperty] private string _note = string.Empty;

    /// <summary>這個站點底下的引擎（＝原本的用途 task）。</summary>
    public ObservableCollection<ModelPoolRow> Engines { get; } = new();

    [RelayCommand]
    private async Task OpenDetailAsync()
    {
        if (_openDetail is not null) await _openDetail(this).ConfigureAwait(true);
    }

    /// <summary>把同一站點的多個 pool 併成一張卡。</summary>
    public static StationRow From(
        string groupKey,
        IReadOnlyList<ModelPoolItem> pools,
        IReadOnlyList<RecentInferenceRow> recent,
        Func<ModelPoolRow, Task> applyVersion,
        Func<StationRow, Task> openDetail)
    {
        var first = pools[0];
        var row = new StationRow
        {
            _openDetail = openDetail,
            GroupKey = groupKey,
            GroupName = string.IsNullOrWhiteSpace(first.GroupName) ? (first.Task ?? groupKey) : first.GroupName!,
        };
        foreach (var p in pools)
            row.Engines.Add(ModelPoolRow.From(p, applyVersion));
        row.UpdateLive(pools, recent);
        return row;
    }

    /// <summary>更新會動的欄位（不重建引擎清單，才不會洗掉使用者的下拉選擇）。</summary>
    public void UpdateLive(IReadOnlyList<ModelPoolItem> pools, IReadOnlyList<RecentInferenceRow> recent)
    {
        foreach (var (engine, p) in Engines.Zip(pools))
            engine.UpdateLive(p);

        InferReady = pools.Any(p => p.InferReady);

        // 「現在在用什麼」：以有現用版本、且可推論的引擎為準；多個就都列出來。
        var actives = pools
            .Where(p => p.InferReady && !string.IsNullOrWhiteSpace(p.CurrentVersion))
            .Select(p => $"{p.EngineName} · {p.CurrentVersion}")
            .ToList();
        CurrentText = actives.Count > 0 ? string.Join("　|　", actives) : "（未設定現用版本）";

        var versionTotal = pools.Sum(p => p.Versions?.Count ?? 0);
        EngineSummary = $"{pools.Count} 個引擎 · {versionTotal} 個版本";

        var mine = recent.Where(r => pools.Any(p =>
            string.Equals(p.Task, r.Task, StringComparison.OrdinalIgnoreCase))).ToList();
        RecentCount = mine.Count;
        LastText = mine.Count > 0 ? $"{mine[0].Time}　{mine[0].Reading}" : "尚無紀錄";

        // 只在「不能推論」時才把 note 拉到首頁——能跑的站點首頁不需要囉嗦。
        Note = InferReady ? string.Empty : (pools.Select(p => p.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "");
    }

    /// <summary>
    /// 給自動化／朗讀程式看的名稱。不覆寫的話它們讀到的是型別名
    /// <c>AIVision.Presentation.Server.ViewModels.StationRow</c> 而不是「模號穴號」。
    /// （2026-08-22 已替 <c>TrainingRunRow</c>／<c>RecentInferenceRow</c> 補過，這個當時漏了。）
    /// </summary>
    public override string ToString() => $"{GroupName}　{CurrentText}";
}
