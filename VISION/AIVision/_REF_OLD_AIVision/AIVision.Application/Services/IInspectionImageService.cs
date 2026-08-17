namespace AIVision.Application.Services;

using AIVision.Domain.Shared;

/// <summary>
/// 檢測圖片保存服務
/// </summary>
public interface IInspectionImageService
{
    /// <summary>保存檢測圖片（原始圖和標註圖）</summary>
    /// <returns>返回 (原始圖路徑, 標註圖路徑)</returns>
    Task<(string originalPath, string? annotatedPath)> SaveInspectionImageAsync(
        ImageData originalImage,
        string workOrderCode,
        string result,
        ImageData? annotatedImage,
        CancellationToken cancellationToken);

    /// <summary>
    /// 為工單預先創建 OK 和 NG 資料夾
    /// 在創建工單時呼叫，確保資料夾結構存在
    /// </summary>
    /// <param name="workOrderCode">工單代碼</param>
    void EnsureWorkOrderFoldersExist(string workOrderCode);
}
