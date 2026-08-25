using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIVision.Infrastructure.MoldCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Server.ViewModels;

/// <summary>
/// 自我強化訓練（父端畫面）。
///
/// <para><b>這頁在做什麼</b>：把產線抓到的混料圖（自帶正解）拿來補強模型。
/// 流程是 <b>選資料集 → 訓練 → 過閘門 → 你按上架</b>。</para>
///
/// <para><b>刻意保留的兩道人為關卡</b>（照抄 <c>模號檢驗/相機版</c> 的設計）：</para>
/// <list type="number">
/// <item>訓練<b>永不覆蓋</b>現有權重，一律開新 run。</item>
/// <item>過閘門只代表「可以考慮」，<b>要你按「上架」</b>才進登錄庫；
///       上架後<b>還要</b>到模型池按「設為現用」才會真的用它。</item>
/// </list>
/// </summary>
public partial class TrainingViewModel : ObservableObject, IDisposable
{
    private readonly InferenceMonitorClient _monitor;
    private readonly ILogger? _logger;
    private readonly DispatcherTimer _timer;

    public TrainingViewModel(InferenceMonitorClient monitor, ILogger? logger = null)
    {
        _monitor = monitor;
        _logger = logger;

        // 訓練會跑很久，但進度要看得到 → 3 秒更新一次。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public ObservableCollection<TrainingDatasetRow> Datasets { get; } = new();
    public ObservableCollection<TrainingRunRow> Runs { get; } = new();

    /// <summary>可訓練的用途（目前只有這兩個有訓練入口）。</summary>
    public IReadOnlyList<string> Tasks { get; } = new[] { "ocr_pair", "ocr_crnn" };

    /// <summary>ocr_pair 要指定訓哪個 head。</summary>
    public IReadOnlyList<string> Heads { get; } = new[] { "mohao", "xuehao" };

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _statusNote = string.Empty;
    [ObservableProperty] private string _gateText = "-";
    [ObservableProperty] private string _datasetRoot = "-";
    [ObservableProperty] private string _outputRoot = "-";
    [ObservableProperty] private string _lastCheckedText = "-";
    [ObservableProperty] private string _actionMessage = string.Empty;

    // ── 新訓練的輸入 ──────────────────────────
    [ObservableProperty] private string _selectedTask = "ocr_pair";
    [ObservableProperty] private string _selectedHead = "mohao";
    [ObservableProperty] private TrainingDatasetRow? _selectedDataset;
    [ObservableProperty] private string _runName = "";
    [ObservableProperty] private string _notes = "";

    /// <summary>目前選到的 run（右邊看它的執行紀錄）。</summary>
    [ObservableProperty] private TrainingRunRow? _selectedRun;
    [ObservableProperty] private string _selectedRunLog = "";

    /// <summary>ocr_crnn 不分 head，選它時把 head 下拉關掉。</summary>
    public bool NeedsHead => string.Equals(SelectedTask, "ocr_pair", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedTaskChanged(string value) => OnPropertyChanged(nameof(NeedsHead));

    partial void OnSelectedRunChanged(TrainingRunRow? value)
    {
        _lastLoggedState = null;   // 換 run → 強制重載
        if (value is not null) _ = LoadRunLogAsync(value.Id);
        else SelectedRunLog = "";
    }

    /// <summary>上次載入執行紀錄時該 run 的狀態，用來決定要不要重載。</summary>
    private string? _lastLoggedState;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var status = await _monitor.GetTrainingStatusAsync().ConfigureAwait(true);
        LastCheckedText = DateTime.Now.ToString("HH:mm:ss");

        if (status is null)
        {
            Enabled = false;
            StatusNote = "連不上推論服務，或這台的 API 版本還沒有訓練端點。";
            return;
        }

        Enabled = status.Enabled;
        Busy = status.Busy;
        DatasetRoot = status.DatasetRoot ?? "-";
        OutputRoot = status.OutputRoot ?? "-";
        GateText =
            $"YOLO：目標 recall ≥ {status.YoloMinTargetRecall:P0}、誤報 ≤ {status.YoloMaxFalsePositiveRate:P0}　|　" +
            $"CRNN：選定集 ≥ {status.CrnnMinSelectedAccuracy:P0}、排練集退步 ≤ {status.CrnnMaxRehearsalRegression:P0}";
        StatusNote = status.Note ?? BuildReadyNote(status);

        await RefreshDatasetsAsync().ConfigureAwait(true);
        await RefreshRunsAsync().ConfigureAwait(true);
    }

    private static string BuildReadyNote(TrainingStatusInfo s)
    {
        var parts = new List<string>();
        if (!s.YoloEntryReady) parts.Add("YOLO 訓練入口未設定");
        if (!s.CrnnEntryReady) parts.Add("CRNN 訓練入口未設定");
        if (!s.RehearsalReady) parts.Add("CRNN 排練集未設定（CRNN 會被擋）");
        return parts.Count == 0
            ? $"就緒（device={s.Device}、epochs={s.Epochs}、最少 {s.MinImages} 張）"
            : "⚠ " + string.Join("；", parts);
    }

    private async Task RefreshDatasetsAsync()
    {
        var dto = await _monitor.GetDatasetsAsync().ConfigureAwait(true);
        if (dto is null) return;

        var incoming = dto.Datasets ?? new List<TrainingDatasetInfo>();
        // 只在清單真的變了才重建——否則每 3 秒會把使用者的選擇洗掉。
        if (Datasets.Count == incoming.Count &&
            Datasets.Zip(incoming).All(x => x.First.Name == x.Second.Name
                                            && x.First.ImageCount == x.Second.ImageCount))
            return;

        var keep = SelectedDataset?.Name;
        Datasets.Clear();
        foreach (var d in incoming)
            Datasets.Add(new TrainingDatasetRow
            {
                Name = d.Name ?? "",
                Path = d.Path ?? "",
                ImageCount = d.ImageCount,
                Summary = $"{d.Name}（{d.ImageCount} 張）",
            });
        SelectedDataset = Datasets.FirstOrDefault(x => x.Name == keep) ?? Datasets.FirstOrDefault();
    }

    private async Task RefreshRunsAsync()
    {
        var dto = await _monitor.GetTrainingRunsAsync().ConfigureAwait(true);
        if (dto is null) return;

        Busy = dto.Busy;
        var incoming = dto.Runs ?? new List<TrainingRunInfo>();

        // run 的狀態每次都會動（進度/訊息），所以逐列更新而不是整個重建。
        if (Runs.Count == incoming.Count &&
            Runs.Zip(incoming).All(x => x.First.Id == x.Second.Id))
        {
            foreach (var (row, info) in Runs.Zip(incoming)) row.Update(info);
            await RefreshSelectedLogAsync().ConfigureAwait(true);
            return;
        }

        var keep = SelectedRun?.Id;
        Runs.Clear();
        foreach (var r in incoming) Runs.Add(TrainingRunRow.From(r));
        SelectedRun = Runs.FirstOrDefault(x => x.Id == keep) ?? Runs.FirstOrDefault();
        await RefreshSelectedLogAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 訓練跑起來要看得到 log 在動。
    /// <para>⚠ 原本只在「換選 run」時載入一次 → 真的跑 30 分鐘的訓練會看到執行紀錄
    /// **卡在最前面幾行**（2026-08-22 走 UI 自測時抓到）。</para>
    /// <para>只在「還在跑」或「狀態剛變了」時重載——跑完的 run 不必每 3 秒重抓一次。</para>
    /// </summary>
    private async Task RefreshSelectedLogAsync()
    {
        var run = SelectedRun;
        if (run is null) return;
        if (!run.IsRunning && _lastLoggedState == run.State) return;
        _lastLoggedState = run.State;
        await LoadRunLogAsync(run.Id).ConfigureAwait(true);
    }

    private async Task LoadRunLogAsync(string id)
    {
        var run = await _monitor.GetTrainingRunAsync(id, 300).ConfigureAwait(true);
        SelectedRunLog = run?.Log is { Count: > 0 }
            ? string.Join(Environment.NewLine, run.Log)
            : "（尚無執行紀錄）";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedDataset is null)
        {
            ActionMessage = "請先選一個資料集（站端把混料圖上傳後才會出現）。";
            return;
        }
        if (string.IsNullOrWhiteSpace(RunName))
        {
            ActionMessage = "請填 run 名稱——它同時是輸出資料夾名與上架時的預設版本名。";
            return;
        }

        ActionMessage = $"送出訓練 {RunName}…";
        var (run, error) = await _monitor.StartTrainingAsync(new
        {
            task = SelectedTask,
            head = NeedsHead ? SelectedHead : "",
            dataset = SelectedDataset.Name,
            runName = RunName.Trim(),
            notes = Notes,
        }).ConfigureAwait(true);

        if (error is not null)
        {
            ActionMessage = $"⚠ 無法開始：{error}";
            return;
        }
        ActionMessage = $"✔ 已開始訓練 {run?.Id}。訓練期間會獨佔 GPU，請等它跑完再送下一個。";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (SelectedRun is null) return;
        var err = await _monitor.CancelTrainingAsync(SelectedRun.Id).ConfigureAwait(true);
        ActionMessage = err is null ? $"已要求取消 {SelectedRun.Id}。" : $"⚠ 取消失敗：{err}";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (SelectedRun is null) return;
        if (!SelectedRun.CanPublish)
        {
            ActionMessage = $"⚠ {SelectedRun.Id} 不能上架（狀態 {SelectedRun.State}）。只有**通過驗證閘門**的候選可以。";
            return;
        }

        ActionMessage = $"上架 {SelectedRun.Id}…";
        var err = await _monitor.PublishTrainingRunAsync(SelectedRun.Id, SelectedRun.Id).ConfigureAwait(true);
        ActionMessage = err is null
            ? $"✔ 已上架 {SelectedRun.Id}。⚠ 上架不等於啟用——要到站點細節的模型池按「設為現用」才會真的用它。"
            : $"⚠ 上架失敗：{err}";
        await RefreshAsync().ConfigureAwait(true);
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>可用的訓練資料集。</summary>
public partial class TrainingDatasetRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private int _imageCount;
    [ObservableProperty] private string _summary = "";
}

/// <summary>一次訓練在清單上的樣子。</summary>
public partial class TrainingRunRow : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _task = "";
    [ObservableProperty] private string _head = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _metricsText = "-";
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private bool _canPublish;
    [ObservableProperty] private bool _published;
    [ObservableProperty] private bool _isRunning;

    /// <summary>
    /// DataGrid 列的無障礙名稱預設是型別名（`...ViewModels.TrainingRunRow`），
    /// 螢幕閱讀器與自動化都讀不出是哪一筆（走 UI 自測時發現）。給它一個有意義的名字。
    /// </summary>
    public override string ToString() => $"{Id}　{Task}/{Head}　{StateText}";

    public static TrainingRunRow From(TrainingRunInfo r)
    {
        var row = new TrainingRunRow { Id = r.Id ?? "", Task = r.Task ?? "", Head = r.Head ?? "" };
        row.Update(r);
        row.CreatedText = r.CreatedAt.ToString("MM/dd HH:mm");
        return row;
    }

    public void Update(TrainingRunInfo r)
    {
        State = r.State ?? "";
        Progress = r.Progress;
        Stage = r.Stage ?? "";
        Message = r.Message ?? "";
        CanPublish = r.CanPublish;
        Published = r.Published;
        IsRunning = State is "Running" or "Queued";
        StateText = State switch
        {
            "Queued" => "等待中",
            "Running" => $"訓練中 {Progress}%",
            "Passed" => r.Published ? $"已上架（{r.PublishedVersion}）" : "通過驗證 · 可上架",
            "Failed" => "未通過驗證",
            "Error" => "執行錯誤",
            "Cancelled" => "已取消",
            _ => State,
        };
        MetricsText = r.Metrics is { Count: > 0 }
            ? string.Join("　", r.Metrics.Select(m => $"{m.Key}={m.Value:0.###}"))
            : "-";
    }
}
