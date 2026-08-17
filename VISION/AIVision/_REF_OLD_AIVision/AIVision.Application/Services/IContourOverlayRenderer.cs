using AIVision.Domain.Shared;

namespace AIVision.Application.Services;

/// <summary>
/// 瑕疵輪廓疊加渲染器介面
/// </summary>
public interface IContourOverlayRenderer
{
    /// <summary>
    /// 在原始圖片上繪製瑕疵輪廓
    /// </summary>
    /// <param name="original">原始圖片資料</param>
    /// <param name="defects">瑕疵清單</param>
    /// <returns>已標註瑕疵輪廓的圖片資料</returns>
    ImageData DrawDefectContours(ImageData original, IReadOnlyList<WorkflowDefect> defects);
}
