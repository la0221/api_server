using AIVision.Domain.Entities;
using AIVision.Domain.Shared;
using System.Windows.Media.Imaging;  // WPF 類型用於 UI 即時顯示

namespace AIVision.Domain.AutoRun;

/// <summary>
/// Auto Run 狀態變更事件參數
/// </summary>
public class AutoRunStateChangedEventArgs : EventArgs
{
    public AutoRunState PreviousState { get; }
    public AutoRunState NewState { get; }
    public DateTime Timestamp { get; }

    public AutoRunStateChangedEventArgs(AutoRunState previousState, AutoRunState newState)
    {
        PreviousState = previousState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// 檢測完成事件參數
/// </summary>
public class InspectionCompletedEventArgs : EventArgs
{
    /// <summary>檢測序號</summary>
    public int InspectionIndex { get; }

    /// <summary>是否合格</summary>
    public bool IsOk { get; }

    /// <summary>結果標籤</summary>
    public string Label { get; }

    /// <summary>信心度 (0-1)</summary>
    public float Confidence { get; }

    /// <summary>取像時間 (ms)</summary>
    public long CaptureTimeMs { get; }

    /// <summary>推論時間 (ms)</summary>
    public long InferenceTimeMs { get; }

    /// <summary>總耗時 (ms)</summary>
    public long TotalTimeMs { get; }

    /// <summary>原始圖片路徑</summary>
    public string? ImagePath { get; }

    /// <summary>標註圖片路徑</summary>
    public string? AnnotatedImagePath { get; }

    /// <summary>標註圖的 BitmapSource（用於 UI 即時顯示，避免重複檔案 I/O）</summary>
    public BitmapSource? AnnotatedBitmap { get; }

    /// <summary>瑕疵清單（用於缺陷統計）</summary>
    public IReadOnlyList<Defect>? Defects { get; }

    /// <summary>時間戳記</summary>
    public DateTime Timestamp { get; }

    public InspectionCompletedEventArgs(
        int inspectionIndex,
        bool isOk,
        string label,
        float confidence,
        long captureTimeMs,
        long inferenceTimeMs,
        long totalTimeMs,
        string? imagePath = null,
        string? annotatedImagePath = null,
        BitmapSource? annotatedBitmap = null,
        IReadOnlyList<Defect>? defects = null)
    {
        InspectionIndex = inspectionIndex;
        IsOk = isOk;
        Label = label;
        Confidence = confidence;
        CaptureTimeMs = captureTimeMs;
        InferenceTimeMs = inferenceTimeMs;
        TotalTimeMs = totalTimeMs;
        ImagePath = imagePath;
        AnnotatedImagePath = annotatedImagePath;
        AnnotatedBitmap = annotatedBitmap;
        Defects = defects;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Auto Run 錯誤事件參數
/// </summary>
public class AutoRunErrorEventArgs : EventArgs
{
    /// <summary>錯誤類型</summary>
    public AutoRunErrorType ErrorType { get; }

    /// <summary>錯誤訊息</summary>
    public string Message { get; }

    /// <summary>例外物件</summary>
    public Exception? Exception { get; }

    /// <summary>發生時的狀態</summary>
    public AutoRunState StateWhenOccurred { get; }

    /// <summary>是否可恢復</summary>
    public bool IsRecoverable { get; }

    /// <summary>重試次數</summary>
    public int RetryCount { get; }

    /// <summary>時間戳記</summary>
    public DateTime Timestamp { get; }

    public AutoRunErrorEventArgs(
        AutoRunErrorType errorType,
        string message,
        Exception? exception,
        AutoRunState stateWhenOccurred,
        bool isRecoverable = true,
        int retryCount = 0)
    {
        ErrorType = errorType;
        Message = message;
        Exception = exception;
        StateWhenOccurred = stateWhenOccurred;
        IsRecoverable = isRecoverable;
        RetryCount = retryCount;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Auto Run 錯誤類型
/// </summary>
public enum AutoRunErrorType
{
    /// <summary>未知錯誤</summary>
    Unknown,

    /// <summary>PLC 連線錯誤</summary>
    PlcConnection,

    /// <summary>PLC 通訊錯誤</summary>
    PlcCommunication,

    /// <summary>相機連線錯誤</summary>
    CameraConnection,

    /// <summary>取像錯誤</summary>
    CaptureError,

    /// <summary>取像超時</summary>
    CaptureTimeout,

    /// <summary>推論服務錯誤</summary>
    InferenceService,

    /// <summary>推論超時</summary>
    InferenceTimeout,

    /// <summary>儲存錯誤</summary>
    SaveError,

    /// <summary>設定錯誤</summary>
    ConfigurationError
}

/// <summary>
/// 取像完成事件參數
/// </summary>
public class CaptureCompletedEventArgs : EventArgs
{
    /// <summary>影像資料</summary>
    public ImageData Image { get; }

    /// <summary>影像寬度</summary>
    public int Width => Image.Width;

    /// <summary>影像高度</summary>
    public int Height => Image.Height;

    /// <summary>取像耗時 (ms)</summary>
    public long ElapsedMs { get; }

    /// <summary>時間戳記</summary>
    public DateTime Timestamp { get; }

    public CaptureCompletedEventArgs(ImageData image, long elapsedMs)
    {
        Image = image;
        ElapsedMs = elapsedMs;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// 觸發接收事件參數
/// </summary>
public class TriggerReceivedEventArgs : EventArgs
{
    /// <summary>觸發序號</summary>
    public int TriggerIndex { get; }

    /// <summary>時間戳記</summary>
    public DateTime Timestamp { get; }

    public TriggerReceivedEventArgs(int triggerIndex)
    {
        TriggerIndex = triggerIndex;
        Timestamp = DateTime.Now;
    }
}
