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
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Server.ViewModels;

/// <summary>
/// 中央推論機（父端）主畫面 ViewModel——看「這台機器」的推論服務狀態、站點與收件紀錄。
/// <para>
/// 健檢走 <c>GET /api/infer/ocr_crnn/health</c>：status = disabled｜cold（池空，首發會冷啟 20-90 秒）｜ready。
/// 位址預設 localhost（本程式就裝在中央機上），可於 appsettings.json 的 InferenceServer:BaseUrl 調整。
/// </para>
/// <para>
/// <b>版面原則（2026-08-19 使用者指定）</b>：首頁只放一眼要知道的，**細節一律點進去**——
/// 站點卡 → 站點細節（條列式：引擎與模型／收到的圖與存放點／最近辨識）→ 單筆詳細。
/// 站點＝模號穴號／公母模／瑕疵檢查；**模號穴號不管走 CRNN 還是雙 head 都是同一個站點**，
/// 引擎收進卡片裡，不並排成兩張卡。
/// </para>
/// </summary>
public partial class ServerMonitorViewModel : ObservableObject, IDisposable
{
    private readonly CrnnInferClient _client;
    private readonly InferenceMonitorClient _monitor;
    private readonly InferenceServerOptions _options;
    private readonly ILogger<ServerMonitorViewModel>? _logger;
    private readonly DispatcherTimer _timer;

    /// <summary>開站點細節視窗（由 View 注入，VM 不直接碰 Window）。</summary>
    public Func<StationRow, Task>? OpenStationDetail { get; set; }

    /// <summary>開單筆詳細視窗。</summary>
    public Func<RecentInferenceRow, Task>? OpenRecordDetail { get; set; }

    public ServerMonitorViewModel(
        CrnnInferClient client,
        InferenceMonitorClient monitor,
        IOptions<InferenceServerOptions> options,
        ILogger<ServerMonitorViewModel>? logger = null)
    {
        _client = client;
        _monitor = monitor;
        _options = options.Value;
        _logger = logger;
        ServerUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "(未設定)" : _options.BaseUrl!;

        // 心跳走 1 秒一拍、每 5 拍做一次真的查詢：
        // 這樣畫面上的脈動是**由計時器本身驅動**的——計時器若又死掉，脈動會跟著停，
        // 不會再出現「勾選說在自動更新、其實停了」這種只有比對 API 才看得出來的情況。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await OnTickAsync().ConfigureAwait(true);

        // ⚠ 一定要在這裡明確啟動。
        // 原本只靠 OnAutoRefreshChanged 裡的 _timer.Start()，但 _autoRefresh 欄位初值就是 true
        // → 屬性值從沒「改變」過 → 那個 partial method 永遠不會被呼叫 → 計時器根本沒跑。
        // 症狀：畫面上「每 5 秒自動更新」打著勾，資料卻永遠停在 Loaded 那一次
        // （2026-08-24 UI 實測：不碰它 100 秒，時鐘凍結、累計停在 8 而 API 真值是 28）。
        if (AutoRefresh) _timer.Start();
    }

    /// <summary>站點卡（模號穴號／公母模／瑕疵檢查）。</summary>
    public ObservableCollection<StationRow> Stations { get; } = new();

    /// <summary>最近辨識紀錄（全站，點單筆可看詳細）。</summary>
    public ObservableCollection<RecentInferenceRow> RecentItems { get; } = new();

    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _statusText = "尚未檢查";
    [ObservableProperty] private string _statusDetail = "-";
    [ObservableProperty] private string _defaultVersion = "-";
    [ObservableProperty] private string _lastCheckedText = "-";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _autoRefresh = true;

    /// <summary>本機啟動以來累計收到的送檢筆數——「到底有沒有收到」最直接的答案。</summary>
    [ObservableProperty] private long _totalReceived;

    /// <summary>是否留存收到的圖（首頁只顯示狀態，開關在站點細節頁）。</summary>
    [ObservableProperty] private string _imageSaveText = "-";

    [ObservableProperty] private string _actionMessage = string.Empty;

    /// <summary>心跳：每秒翻面一次，畫面拿它做脈動。停止跳動＝更新迴圈死了，一眼看得出來。</summary>
    [ObservableProperty] private bool _heartbeat;

    /// <summary>「更新於 N 秒前」。畫面永遠講出資料有多新，而不是只講「有在自動更新」。</summary>
    [ObservableProperty] private string _freshnessText = "尚未更新";

    /// <summary>資料是否過期（超過 3 個週期沒成功更新）。過期時畫面轉紅字提醒。</summary>
    [ObservableProperty] private bool _isStale;

    private DateTime _lastSuccessAt = DateTime.MinValue;
    private int _tick;

    /// <summary>每秒一拍：更新心跳與新鮮度；每 5 拍做一次真的查詢。</summary>
    private async Task OnTickAsync()
    {
        Heartbeat = !Heartbeat;

        if (_lastSuccessAt == DateTime.MinValue)
        {
            FreshnessText = "尚未更新";
            IsStale = false;
        }
        else
        {
            var age = DateTime.Now - _lastSuccessAt;
            var sec = (int)age.TotalSeconds;
            FreshnessText = sec < 60 ? $"更新於 {sec} 秒前" : $"更新於 {(int)age.TotalMinutes} 分鐘前";
            IsStale = age > TimeSpan.FromSeconds(RefreshSeconds * 3);
        }

        if (++_tick < RefreshSeconds) return;
        _tick = 0;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>幾秒做一次真的查詢。</summary>
    private const int RefreshSeconds = 5;

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value) { _tick = 0; _timer.Start(); }
        else _timer.Stop();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            var health = await _client.CheckHealthAsync(CancellationToken.None).ConfigureAwait(true);
            LastCheckedText = DateTime.Now.ToString("HH:mm:ss");

            if (health is null)
            {
                IsOnline = false;
                IsReady = false;
                StatusText = "推論服務未回應";
                StatusDetail = $"連不上 {ServerUrl}——請確認 API 服務已啟動（本機通常是 AIVision.Api）。";
                Stations.Clear();
                _lastSuccessAt = DateTime.Now;   // 「問到了、答案是連不上」也算資訊是新的
                return;
            }

            IsOnline = true;
            var status = (health.Status ?? "").Trim().ToLowerInvariant();
            IsReady = status == "ready";
            StatusText = status switch
            {
                "ready" => "運作中",
                "cold" => "待機中（尚未載入模型）",
                "disabled" => "已停用",
                _ => string.IsNullOrEmpty(status) ? "未知狀態" : status,
            };
            StatusDetail = status switch
            {
                "cold" => "模型行程池為空：第一張送檢會觸發冷啟（可能 20–90 秒），之後恢復正常速度。",
                "disabled" => "此伺服器未啟用中央推論功能。",
                "ready" => health.Note ?? "模型已載入，可正常接收各站送檢。",
                _ => health.Note ?? "-",
            };
            DefaultVersion = string.IsNullOrWhiteSpace(health.DefaultVersion) ? "-" : health.DefaultVersion!;

            await RefreshRecentAsync().ConfigureAwait(true);
            await RefreshStationsAsync().ConfigureAwait(true);
            await RefreshImageStateAsync().ConfigureAwait(true);

            _lastSuccessAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ServerMonitor] 健檢失敗");
            IsOnline = false;
            IsReady = false;
            StatusText = "檢查失敗";
            StatusDetail = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// 依**站點**重建卡片。
    /// <para>⚠ 只在「站點/引擎/版本清單真的變了」時才重建：這個方法每 5 秒被叫一次，
    /// 無條件 Clear 會把使用者正在操作的下拉選單洗掉。</para>
    /// </summary>
    private async Task RefreshStationsAsync()
    {
        var dto = await _monitor.GetPoolsAsync().ConfigureAwait(true);
        if (dto is null) return;

        // 依站點分組；同站點內維持 server 給的順序（引擎順序固定，畫面才不會跳動）
        var groups = (dto.Pools ?? new List<ModelPoolItem>())
            .GroupBy(p => string.IsNullOrWhiteSpace(p.GroupKey) ? (p.Task ?? "") : p.GroupKey!,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, Pools: g.ToList()))
            .OrderBy(g => StationOrder(g.Key))
            .ToList();

        var recent = RecentItems.ToList();
        var sameShape = Stations.Count == groups.Count &&
                        Stations.Zip(groups).All(x =>
                            string.Equals(x.First.GroupKey, x.Second.Key, StringComparison.OrdinalIgnoreCase) &&
                            x.First.Engines.Count == x.Second.Pools.Count &&
                            x.First.Engines.Zip(x.Second.Pools).All(e =>
                                e.First.Versions.SequenceEqual(e.Second.Versions ?? new List<string>())));

        if (!sameShape)
        {
            Stations.Clear();
            foreach (var g in groups)
                Stations.Add(StationRow.From(g.Key, g.Pools, recent, ApplyVersionAsync, RaiseOpenStationDetail));
            return;
        }

        foreach (var (row, g) in Stations.Zip(groups))
            row.UpdateLive(g.Pools, recent);
    }

    /// <summary>站點顯示順序：模號穴號是主線放最前，未知用途排最後。</summary>
    private static int StationOrder(string groupKey) => groupKey.ToLowerInvariant() switch
    {
        "moldcode" => 0,
        "gongmu" => 1,
        "defect" => 2,
        _ => 99,
    };

    private async Task RefreshRecentAsync()
    {
        var dto = await _monitor.GetRecentAsync(50).ConfigureAwait(true);
        if (dto is null) return;

        TotalReceived = dto.TotalReceived;

        // 只在有新資料時重建清單（避免每 5 秒閃一次、也保住捲動位置）
        var newestSeq = dto.Items.Count > 0 ? dto.Items[0].Seq : 0;
        var currentSeq = RecentItems.Count > 0 ? RecentItems[0].Seq : 0;
        if (newestSeq == currentSeq && RecentItems.Count == dto.Items.Count) return;

        RecentItems.Clear();
        foreach (var it in dto.Items)
            RecentItems.Add(RecentInferenceRow.From(it, RaiseOpenRecordDetail));
    }

    private async Task RefreshImageStateAsync()
    {
        var info = await _monitor.GetImageSettingsAsync().ConfigureAwait(true);
        if (info is null) { ImageSaveText = "-"; return; }
        ImageSaveText = info.Save
            ? $"留存中 · 已存 {info.SavedCount} 張"
            : "不留存（原圖在站端）";
    }

    private Task RaiseOpenStationDetail(StationRow row) =>
        OpenStationDetail?.Invoke(row) ?? Task.CompletedTask;

    private Task RaiseOpenRecordDetail(RecentInferenceRow row) =>
        OpenRecordDetail?.Invoke(row) ?? Task.CompletedTask;

    /// <summary>首頁不放切版 UI，但保留這條路：站點卡建構時要一個 apply 委派。</summary>
    private async Task ApplyVersionAsync(ModelPoolRow row)
    {
        if (row.TaskKey is null || string.IsNullOrWhiteSpace(row.SelectedVersion)) return;
        row.IsApplying = true;
        try
        {
            var err = await _monitor.SetCurrentVersionAsync(row.TaskKey, row.SelectedVersion!)
                .ConfigureAwait(true);
            ActionMessage = err is null
                ? $"✔ {row.EngineName} 已切到 {row.SelectedVersion}"
                : $"⚠ {row.EngineName} 切版失敗：{err}";
        }
        finally
        {
            row.IsApplying = false;
        }
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>
/// 一個**引擎**（原本的用途 task）在畫面上的樣子：現用版本、可選版本、已載入、登錄夾。
/// 站點卡與站點細節頁共用。
/// </summary>
public partial class ModelPoolRow : ObservableObject
{
    private Func<ModelPoolRow, Task>? _apply;

    /// <summary>用途代號（ocr_crnn/ocr_pair/gongmu/defect）。刻意不叫 Task——會跟型別 Task 撞名。</summary>
    [ObservableProperty] private string? _taskKey;

    /// <summary>引擎名（CRNN 字元式／雙 head 分類…）。</summary>
    [ObservableProperty] private string _engineName = string.Empty;

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _root = string.Empty;
    [ObservableProperty] private bool _rootExists;
    [ObservableProperty] private string _currentVersion = "-";
    [ObservableProperty] private string? _selectedVersion;
    [ObservableProperty] private string _loadedText = "-";
    [ObservableProperty] private bool _canSwitch;
    [ObservableProperty] private bool _inferReady;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _filesText = string.Empty;

    public ObservableCollection<string> Versions { get; } = new();

    /// <summary>可以按「設為現用」＝支援切換、有版本、且不在切換中。</summary>
    public bool CanApply => CanSwitch && !IsApplying && Versions.Count > 0;

    partial void OnCanSwitchChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    partial void OnIsApplyingChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_apply is not null) await _apply(this).ConfigureAwait(true);
    }

    public static ModelPoolRow From(ModelPoolItem p, Func<ModelPoolRow, Task> apply)
    {
        var row = new ModelPoolRow
        {
            _apply = apply,
            TaskKey = p.Task,
            EngineName = string.IsNullOrWhiteSpace(p.EngineName) ? (p.Task ?? "-") : p.EngineName!,
            DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? (p.Task ?? "-") : p.DisplayName!,
            Root = p.Root ?? "-",
            FilesText = p.RequiredFiles.Count > 0 ? string.Join(" + ", p.RequiredFiles) : "-",
        };
        foreach (var v in p.Versions ?? new List<string>()) row.Versions.Add(v);
        row.UpdateLive(p);
        row.SelectedVersion = row.Versions.Contains(row.CurrentVersion)
            ? row.CurrentVersion
            : row.Versions.FirstOrDefault();
        return row;
    }

    /// <summary>更新會隨時間變的欄位（不動版本清單，才不會洗掉使用者的下拉選擇）。</summary>
    public void UpdateLive(ModelPoolItem p)
    {
        RootExists = p.RootExists;
        InferReady = p.InferReady;
        CanSwitch = p.CanSwitch;
        CurrentVersion = string.IsNullOrWhiteSpace(p.CurrentVersion) ? "(未設定)" : p.CurrentVersion!;
        LoadedText = p.LoadedVersions is { Count: > 0 }
            ? string.Join("、", p.LoadedVersions.Select(l => l.Version + (l.Ready ? "" : "(載入中)")))
            : "（尚未載入）";
        Note = p.Note ?? (p.Versions is { Count: > 0 } ? $"共 {p.Versions.Count} 個版本" : "");
    }
}

/// <summary>父端實際收到的一筆送檢（清單列；點開看單筆詳細）。</summary>
public partial class RecentInferenceRow : ObservableObject
{
    private Func<RecentInferenceRow, Task>? _openDetail;

    [ObservableProperty] private long _seq;
    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string? _task;
    [ObservableProperty] private string _stationId = string.Empty;

    /// <summary>
    /// 站端的**單片識別碼**（<c>{站號}_{yyyyMMdd}_{流水}</c>）。
    /// <para>兩邊 log 對帳的鑰匙：拿它去站端的 <c>records\</c> 就找得到那一片的原圖、
    /// 前處理圖與完整判定 json。沒有它只能靠時間戳猜。</para>
    /// </summary>
    [ObservableProperty] private string _pieceId = "-";
    [ObservableProperty] private string _reading = string.Empty;
    [ObservableProperty] private bool _hasReading;
    [ObservableProperty] private bool _needsReview;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _preprocessText = string.Empty;
    [ObservableProperty] private string _modelVersion = string.Empty;
    [ObservableProperty] private int _elapsedMs;
    [ObservableProperty] private string _edgeRawPath = string.Empty;
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private bool _ok = true;

    /// <summary>清單上「有沒有留圖」那一格的文字。</summary>
    public string ImageText => HasImage ? "有留存" : "—";

    [RelayCommand]
    private async Task OpenDetailAsync()
    {
        if (_openDetail is not null) await _openDetail(this).ConfigureAwait(true);
    }

    /// <summary>同 TrainingRunRow：讓 DataGrid 列有可讀的無障礙名稱。</summary>
    public override string ToString() => $"{Time}　{StationId}　{Reading}";

    public static RecentInferenceRow From(RecentInferenceItem it, Func<RecentInferenceRow, Task> openDetail) =>
        new()
        {
            _openDetail = openDetail,
            Seq = it.Seq,
            Time = it.Time ?? "",
            Task = it.Task,
            PieceId = string.IsNullOrWhiteSpace(it.PieceId) ? "-" : it.PieceId!,
            StationId = string.IsNullOrWhiteSpace(it.StationId) ? "-" : it.StationId!,
            Reading = it.Reading ?? "-",
            HasReading = it.HasReading,
            NeedsReview = it.NeedsReview,
            SizeText = $"{it.ReceivedBytes / 1000.0:F0} KB",
            PreprocessText = it.IsStrip ? "站端前處理" : "父端前處理",
            ModelVersion = string.IsNullOrWhiteSpace(it.ModelVersion) ? "-" : it.ModelVersion!,
            ElapsedMs = it.ElapsedMs,
            EdgeRawPath = string.IsNullOrWhiteSpace(it.EdgeRawPath) ? "(站端未提供)" : it.EdgeRawPath!,
            HasImage = it.HasImage,
            Ok = it.Ok,
        };
}
