using System;
using System.Collections.Generic;
using System.Linq;

namespace AIVision.Api.Services;

/// <summary>訓練 run 的狀態。</summary>
public enum TrainingRunState
{
    /// <summary>已建立、等待啟動。</summary>
    Queued,

    /// <summary>python 正在跑。</summary>
    Running,

    /// <summary>跑完且**通過驗證閘門**——這才是可以上架的候選。</summary>
    Passed,

    /// <summary>跑完但沒過閘門（準確率不足／排練集退步太多）。權重仍留著供查。</summary>
    Failed,

    /// <summary>執行過程出錯（python 掛掉、逾時、找不到入口）。</summary>
    Error,

    /// <summary>使用者取消。</summary>
    Cancelled,
}

/// <summary>
/// 一次自我強化訓練。
///
/// <para><b>兩條鐵律</b>（照抄 <c>模號檢驗/相機版</c> 的設計，這兩點是整套的價值所在）：</para>
/// <list type="number">
/// <item><b>永不覆蓋 production 權重</b>——一律開新 run 夾，舊模型原封不動。</item>
/// <item><b>過閘門才算候選，使用者再按「上架」才會真的生效</b>——
///       訓練成功 ≠ 自動上線。</item>
/// </list>
/// </summary>
public sealed class TrainingRun
{
    /// <summary>run 識別（＝資料夾名）。</summary>
    public string Id { get; init; } = "";

    /// <summary>用途：<c>ocr_crnn</c> 或 <c>ocr_pair</c>。</summary>
    public string Task { get; init; } = "";

    /// <summary>訓練哪個 head：<c>mohao</c> / <c>xuehao</c>（ocr_pair 用）。</summary>
    public string Head { get; init; } = "";

    /// <summary>資料集路徑（server 上的絕對路徑）。</summary>
    public string DatasetPath { get; init; } = "";

    /// <summary>這次訓練用的資料集張數。</summary>
    public int ImageCount { get; init; }

    /// <summary>備註（為什麼要訓這一版；寫進 manifest 供日後溯源）。</summary>
    public string Notes { get; init; } = "";

    /// <summary>run 輸出資料夾。</summary>
    public string OutputPath { get; init; } = "";

    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public TrainingRunState State { get; set; } = TrainingRunState.Queued;

    /// <summary>0–100，來自 python 的 <c>[PROGRESS] n 訊息</c>。</summary>
    public int Progress { get; set; }

    /// <summary>目前階段的說明（同上）。</summary>
    public string Stage { get; set; } = "";

    /// <summary>結果訊息（過/沒過的原因）。</summary>
    public string Message { get; set; } = "";

    /// <summary>訓練產出的權重檔路徑（沒過閘門也會有，供查）。</summary>
    public string? WeightPath { get; set; }

    /// <summary>python 回報的量測數字（accuracy / recall / rehearsal…）。</summary>
    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>是否已上架到模型登錄庫。</summary>
    public bool Published { get; set; }

    /// <summary>上架後的版本名。</summary>
    public string? PublishedVersion { get; set; }

    /// <summary>執行紀錄（滾動保留最後 N 行）。</summary>
    public List<string> Log { get; } = new();

    /// <summary>log 保留上限——訓練可以跑幾小時，無上限會把記憶體吃光。</summary>
    private const int MaxLogLines = 2000;

    private readonly object _logLock = new();

    public void AppendLog(string line)
    {
        lock (_logLock)
        {
            Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (Log.Count > MaxLogLines)
                Log.RemoveRange(0, Log.Count - MaxLogLines);
        }
    }

    public IReadOnlyList<string> TailLog(int lines)
    {
        lock (_logLock)
            return Log.Skip(Math.Max(0, Log.Count - Math.Max(1, lines))).ToList();
    }

    /// <summary>只有「跑完且過閘門、還沒上架」的 run 可以上架。</summary>
    public bool CanPublish =>
        State == TrainingRunState.Passed && !Published && !string.IsNullOrWhiteSpace(WeightPath);
}
