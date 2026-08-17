using System.Collections.Generic;

namespace AIVision.Presentation.Wpf.Models;

/// <summary>
/// 批量/離線測試頁的「測試資料夾」下拉選項（appsettings 的 <c>TestImageFolders</c>）。
/// 只是 UI 便利選項：欄位仍可手貼任意路徑、或用「選擇資料夾」瀏覽。
/// </summary>
public sealed class TestImageFolderOptions
{
    public const string SectionName = "TestImageFolders";

    /// <summary>常用測試資料夾清單（依現場資料位置增修）。</summary>
    public List<string> Paths { get; set; } = new();
}
