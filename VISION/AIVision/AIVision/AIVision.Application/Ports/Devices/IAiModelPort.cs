using AIVision.Application.Contracts;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// AI 模型掛載 Port，負責載入和切換 AI 模型。
/// </summary>
public interface IAiModelPort
{
    /// <summary>
    /// 載入指定的 AI 模型。
    /// </summary>
    /// <param name="request">模型載入請求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>模型載入結果</returns>
    Task<ModelLoadResult> LoadAsync(ModelLoadRequest request, CancellationToken cancellationToken);
}

