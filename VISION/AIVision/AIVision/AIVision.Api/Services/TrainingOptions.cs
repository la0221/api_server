using System;
using System.IO;

namespace AIVision.Api.Services;

/// <summary>
/// 自我強化訓練設定（appsettings <c>Training</c>）。
/// 移植自 <c>模號檢驗/相機版</c> 的 <c>training_backend.json</c>，跑在**中央推論機**上
/// （GPU、python 環境、模型登錄庫都在這台）。
/// </summary>
public sealed class TrainingOptions
{
    public const string SectionName = "Training";

    /// <summary>未啟用時所有 <c>/api/training/*</c> 端點回 503（不影響其他功能）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>python 執行檔（要有 torch/ultralytics 的那個環境）。</summary>
    public string PythonPath { get; set; } = "python";

    /// <summary>YOLO head 訓練入口（<c>train_yolo_head.py</c> 完整路徑）。</summary>
    public string YoloEntry { get; set; } = "";

    /// <summary>CRNN 訓練入口（<c>train_crnn.py</c> 完整路徑）。</summary>
    public string CrnnEntry { get; set; } = "";

    /// <summary>訓練輸出根目錄；每次訓練在底下開一個新 run 夾。</summary>
    public string OutputRoot { get; set; } = "training_runs";

    /// <summary>上傳資料集的存放根目錄。</summary>
    public string DatasetRoot { get; set; } = "training_datasets";

    /// <summary>現有模號 head 權重（熱啟動基底 / 配對用）。</summary>
    public string YoloMohaoWeights { get; set; } = "";

    /// <summary>現有穴號 head 權重。</summary>
    public string YoloXuehaoWeights { get; set; } = "";

    /// <summary>CRNN 偵測器權重。</summary>
    public string CrnnDetectorWeights { get; set; } = "";

    /// <summary>CRNN 目前的 Non-AR 權重（熱啟動基底）。</summary>
    public string CrnnBaseWeights { get; set; } = "";

    /// <summary>
    /// **CRNN 排練集（rehearsal）路徑——必填**。
    /// <para>這是防「災難性遺忘」的關鍵：只拿一批修正資料去訓練，
    /// 很容易把舊標籤原本會的能力洗掉。訓練後要在這份排練集上再量一次，
    /// 退步超過容忍值就**不予採用**。</para>
    /// </summary>
    public string CrnnRehearsalPath { get; set; } = "";

    public string Device { get; set; } = "auto";
    public int Epochs { get; set; } = 30;
    public int BatchSize { get; set; } = 16;

    /// <summary>資料集最少張數（太少訓不出東西，直接擋在送出前）。</summary>
    public int MinImages { get; set; } = 5;

    // ── 驗證閘門（沿用相機版實測後定的值）──────────────────────────
    /// <summary>CRNN：選定集準確率至少要到多少才算過。</summary>
    public double CrnnMinSelectedAccuracy { get; set; } = 0.90;

    /// <summary>CRNN：排練集允許退步的上限（超過＝把舊能力洗掉了，不採用）。</summary>
    public double CrnnMaxRehearsalRegression { get; set; } = 0.02;

    /// <summary>YOLO：目標類別的 recall 至少要到多少。</summary>
    public double YoloMinTargetRecall { get; set; } = 0.90;

    /// <summary>YOLO：誤報率上限。</summary>
    public double YoloMaxFalsePositiveRate { get; set; } = 0.05;

    /// <summary>單次訓練逾時（毫秒）。預設 6 小時。</summary>
    public int TimeoutMs { get; set; } = 6 * 60 * 60 * 1000;

    /// <summary>保留多少筆 run 記錄在記憶體（磁碟上的 run 夾不受影響）。</summary>
    public int MaxRuns { get; set; } = 200;

    /// <summary>把相對路徑解析成絕對路徑（相對於程式目錄）。</summary>
    public void Normalize()
    {
        OutputRoot = Resolve(OutputRoot, "training_runs");
        DatasetRoot = Resolve(DatasetRoot, "training_datasets");
        Epochs = Math.Clamp(Epochs, 1, 1000);
        BatchSize = Math.Clamp(BatchSize, 1, 512);
        MinImages = Math.Clamp(MinImages, 2, 1_000_000);
        CrnnMinSelectedAccuracy = Math.Clamp(CrnnMinSelectedAccuracy, 0, 1);
        CrnnMaxRehearsalRegression = Math.Clamp(CrnnMaxRehearsalRegression, 0, 1);
        YoloMinTargetRecall = Math.Clamp(YoloMinTargetRecall, 0, 1);
        YoloMaxFalsePositiveRate = Math.Clamp(YoloMaxFalsePositiveRate, 0, 1);
        TimeoutMs = Math.Clamp(TimeoutMs, 60_000, 24 * 60 * 60 * 1000);
        MaxRuns = Math.Clamp(MaxRuns, 10, 10_000);
    }

    private static string Resolve(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) value = fallback;
        try
        {
            return Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, fallback);
        }
    }
}
