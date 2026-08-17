namespace AIVision.Infrastructure.AiService;

/// <summary>
/// 推論記錄服務介面。
/// </summary>
public interface IInferenceLogService
{
    /// <summary>
    /// 新增推論記錄。
    /// </summary>
    /// <param name="result">推論結果</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task AppendLogAsync(PredictResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得所有推論記錄。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>推論記錄列表</returns>
    Task<IReadOnlyList<PredictResult>> GetLogsAsync(CancellationToken cancellationToken = default);
}

