using System.Text.Json;
using AIVision.Application.Configuration;
using AIVision.Application.Services;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 專案配置服務實作 - 管理專案設定檔（project.json）
/// </summary>
public sealed class ProjectConfigService : IProjectConfigService
{
    private readonly ILogger<ProjectConfigService> _logger;
    private readonly string _defaultProjectsFolder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ProjectConfigService(ILogger<ProjectConfigService> logger)
    {
        _logger = logger;

        // 預設專案資料夾：程式目錄下的 Projects 資料夾
        _defaultProjectsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");
    }

    /// <inheritdoc />
    public string DefaultProjectsFolder => _defaultProjectsFolder;

    /// <inheritdoc />
    public ProjectConfig? CurrentProject { get; private set; }

    /// <inheritdoc />
    public event EventHandler<ProjectConfig?>? CurrentProjectChanged;

    /// <inheritdoc />
    public Task<IReadOnlyList<ProjectConfig>> ListProjectsAsync(string? projectsFolder = null)
    {
        var folder = projectsFolder ?? _defaultProjectsFolder;
        var projects = new List<ProjectConfig>();

        _logger.LogInformation("列舉專案資料夾: {Folder}", folder);

        if (!Directory.Exists(folder))
        {
            _logger.LogWarning("專案資料夾不存在: {Folder}", folder);
            return Task.FromResult<IReadOnlyList<ProjectConfig>>(projects);
        }

        foreach (var dir in Directory.GetDirectories(folder))
        {
            var projectFile = Path.Combine(dir, "project.json");
            if (File.Exists(projectFile))
            {
                try
                {
                    var json = File.ReadAllText(projectFile);
                    var project = JsonSerializer.Deserialize<ProjectConfig>(json, JsonOptions);
                    if (project != null)
                    {
                        project.FolderPath = dir;
                        projects.Add(project);
                        _logger.LogDebug("找到專案: {Name} at {Path}", project.Name, dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "無法讀取專案設定: {Path}", projectFile);
                }
            }
        }

        _logger.LogInformation("找到 {Count} 個專案", projects.Count);
        return Task.FromResult<IReadOnlyList<ProjectConfig>>(projects);
    }

    /// <inheritdoc />
    public Task<ProjectConfig> LoadProjectAsync(string projectPath)
    {
        var projectFile = Path.Combine(projectPath, "project.json");

        if (!File.Exists(projectFile))
        {
            throw new FileNotFoundException($"專案設定檔不存在: {projectFile}");
        }

        _logger.LogInformation("載入專案設定: {Path}", projectFile);

        var json = File.ReadAllText(projectFile);
        var project = JsonSerializer.Deserialize<ProjectConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"無法解析專案設定: {projectFile}");

        project.FolderPath = projectPath;

        _logger.LogInformation("專案已載入: {Name}", project.Name);
        return Task.FromResult(project);
    }

    /// <inheritdoc />
    public Task SaveProjectAsync(ProjectConfig project, string? projectsFolder = null)
    {
        var folder = projectsFolder ?? _defaultProjectsFolder;

        // 確保專案資料夾存在
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            _logger.LogInformation("建立專案資料夾: {Folder}", folder);
        }

        // 建立專案子資料夾
        var projectFolder = Path.Combine(folder, SanitizeFolderName(project.Name));
        if (!Directory.Exists(projectFolder))
        {
            Directory.CreateDirectory(projectFolder);
            _logger.LogInformation("建立專案目錄: {Folder}", projectFolder);
        }

        // 儲存 project.json
        var projectFile = Path.Combine(projectFolder, "project.json");
        var json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(projectFile, json);

        // 更新 FolderPath
        project.FolderPath = projectFolder;

        _logger.LogInformation("專案已儲存: {Name} at {Path}", project.Name, projectFile);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteProjectAsync(string projectPath)
    {
        if (!Directory.Exists(projectPath))
        {
            _logger.LogWarning("專案資料夾不存在，無法刪除: {Path}", projectPath);
            return Task.CompletedTask;
        }

        _logger.LogInformation("刪除專案: {Path}", projectPath);

        // 刪除整個專案資料夾
        Directory.Delete(projectPath, recursive: true);

        // 如果是目前載入的專案，清除
        if (CurrentProject?.FolderPath == projectPath)
        {
            SetCurrentProject(null);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetCurrentProject(ProjectConfig? project)
    {
        if (CurrentProject == project) return;

        CurrentProject = project;
        _logger.LogInformation("目前專案已設定: {Name}", project?.Name ?? "(無)");
        CurrentProjectChanged?.Invoke(this, project);
    }

    /// <summary>
    /// 清理資料夾名稱中的非法字元
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());

        return sanitized.Trim();
    }
}
