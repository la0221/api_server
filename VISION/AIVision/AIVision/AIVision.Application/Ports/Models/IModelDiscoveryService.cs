namespace AIVision.Application.Ports.Models;

/// <summary>
/// 模型發現服務介面
/// </summary>
public interface IModelDiscoveryService
{
    /// <summary>
    /// 掃描指定資料夾中的所有模型
    /// </summary>
    /// <param name="folderPath">模型資料夾路徑</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>發現的模型列表</returns>
    Task<IReadOnlyList<DiscoveredModel>> ScanAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// 掃描單一模型資料夾
    /// </summary>
    /// <param name="modelFolderPath">單一模型資料夾路徑</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>模型資訊，若無效則為 null</returns>
    Task<DiscoveredModel?> ScanSingleAsync(string modelFolderPath, CancellationToken ct = default);
}
