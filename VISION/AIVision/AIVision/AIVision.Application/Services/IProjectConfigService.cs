using AIVision.Application.Configuration;

namespace AIVision.Application.Services;

/// <summary>
/// 專案配置服務介面 - 管理專案設定檔（project.json）的載入、儲存、列舉
/// </summary>
public interface IProjectConfigService
{
    /// <summary>
    /// 取得預設專案資料夾路徑
    /// </summary>
    string DefaultProjectsFolder { get; }

    /// <summary>
    /// 目前載入的專案（null 表示尚未載入任何專案）
    /// </summary>
    ProjectConfig? CurrentProject { get; }

    /// <summary>
    /// 列舉指定資料夾中的所有專案
    /// </summary>
    /// <param name="projectsFolder">專案資料夾路徑（null 使用預設資料夾）</param>
    /// <returns>專案列表</returns>
    Task<IReadOnlyList<ProjectConfig>> ListProjectsAsync(string? projectsFolder = null);

    /// <summary>
    /// 載入指定專案的設定
    /// </summary>
    /// <param name="projectPath">專案資料夾路徑</param>
    /// <returns>專案設定</returns>
    Task<ProjectConfig> LoadProjectAsync(string projectPath);

    /// <summary>
    /// 儲存專案設定
    /// </summary>
    /// <param name="project">專案設定</param>
    /// <param name="projectsFolder">專案資料夾路徑（null 使用預設資料夾）</param>
    Task SaveProjectAsync(ProjectConfig project, string? projectsFolder = null);

    /// <summary>
    /// 刪除專案
    /// </summary>
    /// <param name="projectPath">專案資料夾路徑</param>
    Task DeleteProjectAsync(string projectPath);

    /// <summary>
    /// 設定目前載入的專案
    /// </summary>
    /// <param name="project">專案設定</param>
    void SetCurrentProject(ProjectConfig? project);

    /// <summary>
    /// 專案變更事件
    /// </summary>
    event EventHandler<ProjectConfig?>? CurrentProjectChanged;
}
