using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.Devices;

public interface IAiInferencePort
{
    /// <summary>
    /// 檢查 AI 推論服務是否已連線/可用
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 執行 AI 推論
    /// </summary>
    Task<Prediction> PredictAsync(ImageData image, CancellationToken cancellationToken);

    /// <summary>
    /// 健康檢查：驗證 AI 服務是否可達且正常運作
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>服務是否可用</returns>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
