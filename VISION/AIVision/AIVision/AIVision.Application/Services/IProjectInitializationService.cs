using AIVision.Application.Configuration;

namespace AIVision.Application.Services;

/// <summary>
/// 專案初始化服務介面 - 負責根據專案設定初始化所有硬體設備
/// </summary>
public interface IProjectInitializationService
{
    /// <summary>
    /// 目前初始化狀態
    /// </summary>
    ProjectInitializationStatus Status { get; }

    /// <summary>
    /// 執行專案初始化（並行初始化所有設備）
    /// </summary>
    /// <param name="project">專案設定</param>
    /// <param name="ct">取消 Token</param>
    /// <returns>初始化結果</returns>
    Task<ProjectInitializationResult> InitializeAsync(ProjectConfig project, CancellationToken ct = default);

    /// <summary>
    /// 重試失敗的設備
    /// </summary>
    /// <param name="ct">取消 Token</param>
    Task RetryFailedDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止所有設備連線
    /// </summary>
    Task StopAllAsync(CancellationToken ct = default);

    /// <summary>
    /// 狀態變更事件
    /// </summary>
    event EventHandler<ProjectInitializationStatus>? StatusChanged;
}

/// <summary>
/// 專案初始化狀態
/// </summary>
public class ProjectInitializationStatus
{
    /// <summary>
    /// 目前正在初始化的專案名稱
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// PLC 狀態
    /// </summary>
    public DeviceInitStatus Plc { get; set; } = new("PLC");

    /// <summary>
    /// 相機狀態
    /// </summary>
    public DeviceInitStatus Camera { get; set; } = new("相機");

    /// <summary>
    /// 光源狀態
    /// </summary>
    public DeviceInitStatus Light { get; set; } = new("光源");

    /// <summary>
    /// AI 模型狀態
    /// </summary>
    public DeviceInitStatus AiModel { get; set; } = new("AI 模型");

    /// <summary>
    /// 目前進行的任務說明
    /// </summary>
    public string CurrentTask { get; set; } = string.Empty;

    /// <summary>
    /// 總進度百分比 (0-100)
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// 是否所有設備都成功
    /// </summary>
    public bool IsAllSuccess => Plc.IsSuccess && Camera.IsSuccess && Light.IsSuccess && AiModel.IsSuccess;

    /// <summary>
    /// 是否有失敗的設備
    /// </summary>
    public bool HasFailure => Plc.IsError || Camera.IsError || Light.IsError || AiModel.IsError;

    /// <summary>
    /// 是否全部完成（成功或失敗都算完成）
    /// </summary>
    public bool IsComplete => Plc.IsComplete && Camera.IsComplete && Light.IsComplete && AiModel.IsComplete;

    /// <summary>
    /// 取得所有失敗的設備名稱
    /// </summary>
    public IReadOnlyList<string> GetFailedDevices()
    {
        var failed = new List<string>();
        if (Plc.IsError) failed.Add(Plc.Name);
        if (Camera.IsError) failed.Add(Camera.Name);
        if (Light.IsError) failed.Add(Light.Name);
        if (AiModel.IsError) failed.Add(AiModel.Name);
        return failed;
    }

    /// <summary>
    /// 取得所有錯誤訊息
    /// </summary>
    public IReadOnlyList<string> GetErrorMessages()
    {
        var errors = new List<string>();
        if (Plc.IsError && !string.IsNullOrEmpty(Plc.ErrorMessage))
            errors.Add($"PLC: {Plc.ErrorMessage}");
        if (Camera.IsError && !string.IsNullOrEmpty(Camera.ErrorMessage))
            errors.Add($"相機: {Camera.ErrorMessage}");
        if (Light.IsError && !string.IsNullOrEmpty(Light.ErrorMessage))
            errors.Add($"光源: {Light.ErrorMessage}");
        if (AiModel.IsError && !string.IsNullOrEmpty(AiModel.ErrorMessage))
            errors.Add($"AI 模型: {AiModel.ErrorMessage}");
        return errors;
    }
}

/// <summary>
/// 單一設備初始化狀態
/// </summary>
public class DeviceInitStatus
{
    /// <summary>
    /// 設備名稱
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 狀態
    /// </summary>
    public DeviceInitState State { get; set; } = DeviceInitState.Pending;

    /// <summary>
    /// 錯誤訊息（State 為 Error 時有值）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 完成時間
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 耗時（毫秒）
    /// </summary>
    public long? ElapsedMs { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess => State == DeviceInitState.Success;

    /// <summary>
    /// 是否失敗
    /// </summary>
    public bool IsError => State == DeviceInitState.Error;

    /// <summary>
    /// 是否完成（成功或失敗）
    /// </summary>
    public bool IsComplete => State == DeviceInitState.Success || State == DeviceInitState.Error || State == DeviceInitState.Skipped;

    public DeviceInitStatus(string name) => Name = name;

    /// <summary>
    /// 重置狀態
    /// </summary>
    public void Reset()
    {
        State = DeviceInitState.Pending;
        ErrorMessage = null;
        CompletedAt = null;
        ElapsedMs = null;
    }
}

/// <summary>
/// 設備初始化狀態枚舉
/// </summary>
public enum DeviceInitState
{
    /// <summary>
    /// 等待中（灰色）
    /// </summary>
    Pending,

    /// <summary>
    /// 執行中（橘色）
    /// </summary>
    InProgress,

    /// <summary>
    /// 成功（綠色）
    /// </summary>
    Success,

    /// <summary>
    /// 失敗（紅色）
    /// </summary>
    Error,

    /// <summary>
    /// 跳過（灰色，設備未配置）
    /// </summary>
    Skipped
}

/// <summary>
/// 專案初始化結果
/// </summary>
public class ProjectInitializationResult
{
    /// <summary>
    /// 是否全部成功
    /// </summary>
    public bool AllSuccess { get; set; }

    /// <summary>
    /// 失敗的設備列表
    /// </summary>
    public List<string> FailedDevices { get; set; } = new();

    /// <summary>
    /// 錯誤訊息列表
    /// </summary>
    public List<string> ErrorMessages { get; set; } = new();

    /// <summary>
    /// 總耗時（毫秒）
    /// </summary>
    public long TotalElapsedMs { get; set; }
}
