using System.Collections.ObjectModel;
using System.IO;
using AIVision.Application.Configuration;
using AIVision.Application.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using AIVision.Presentation.Wpf.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 專案選擇視窗 ViewModel
/// </summary>
public partial class ProjectSelectViewModel : ObservableObject
{
    private readonly IProjectConfigService _projectConfigService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<ProjectSelectViewModel> _logger;

    [ObservableProperty]
    private string _projectsFolder = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ProjectConfig> _projects = new();

    [ObservableProperty]
    private ProjectConfig? _selectedProject;

    [ObservableProperty]
    private bool _isLoading;

    public bool HasSelectedProject => SelectedProject != null;

    public event Action<bool>? RequestClose;

    public ProjectSelectViewModel(
        IProjectConfigService projectConfigService,
        INavigationService navigationService,
        ILogger<ProjectSelectViewModel> logger)
    {
        _projectConfigService = projectConfigService;
        _navigationService = navigationService;
        _logger = logger;

        // 使用預設專案資料夾
        _projectsFolder = _projectConfigService.DefaultProjectsFolder;

        // 初始載入專案列表
        _ = LoadProjectsAsync();
    }

    partial void OnSelectedProjectChanged(ProjectConfig? value)
    {
        OnPropertyChanged(nameof(HasSelectedProject));
    }

    /// <summary>
    /// 載入專案列表
    /// </summary>
    private async Task LoadProjectsAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("載入專案列表: {Folder}", ProjectsFolder);

            var projectList = await _projectConfigService.ListProjectsAsync(ProjectsFolder);

            Projects.Clear();
            foreach (var project in projectList)
            {
                Projects.Add(project);
            }

            _logger.LogInformation("載入 {Count} 個專案", Projects.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入專案列表失敗");
            System.Windows.MessageBox.Show(
                $"載入專案列表失敗：\n{ex.Message}",
                "錯誤",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 瀏覽選擇專案資料夾
    /// </summary>
    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "選擇專案資料夾",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(ProjectsFolder) ? ProjectsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ProjectsFolder = dialog.SelectedPath;
            await LoadProjectsAsync();
        }
    }

    /// <summary>
    /// 重新整理專案列表
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadProjectsAsync();
    }

    /// <summary>
    /// 新增專案
    /// </summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        try
        {
            var result = _navigationService.ShowDialog<ProjectEditWindow>(window =>
            {
                if (window.DataContext is ProjectEditViewModel vm)
                {
                    vm.Initialize(ProjectsFolder, null);
                }
            });

            if (result == true)
            {
                // 重新載入專案列表
                await LoadProjectsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增專案失敗");
            System.Windows.MessageBox.Show(
                $"新增專案失敗：\n{ex.Message}",
                "錯誤",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 編輯選擇的專案
    /// </summary>
    [RelayCommand]
    private async Task EditProjectAsync()
    {
        if (SelectedProject == null) return;

        try
        {
            var result = _navigationService.ShowDialog<ProjectEditWindow>(window =>
            {
                if (window.DataContext is ProjectEditViewModel vm)
                {
                    vm.Initialize(ProjectsFolder, SelectedProject);
                }
            });

            if (result == true)
            {
                // 重新載入專案列表
                await LoadProjectsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "編輯專案失敗");
            System.Windows.MessageBox.Show(
                $"編輯專案失敗：\n{ex.Message}",
                "錯誤",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 刪除選擇的專案
    /// </summary>
    [RelayCommand]
    private async Task DeleteProjectAsync()
    {
        if (SelectedProject == null) return;

        var result = System.Windows.MessageBox.Show(
            $"確定要刪除專案「{SelectedProject.Name}」嗎？\n\n此操作將刪除整個專案資料夾，無法復原。",
            "確認刪除",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            if (!string.IsNullOrEmpty(SelectedProject.FolderPath))
            {
                await _projectConfigService.DeleteProjectAsync(SelectedProject.FolderPath);
            }

            // 重新載入專案列表
            await LoadProjectsAsync();
            SelectedProject = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除專案失敗");
            System.Windows.MessageBox.Show(
                $"刪除專案失敗：\n{ex.Message}",
                "錯誤",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 載入選擇的專案
    /// </summary>
    [RelayCommand]
    private void LoadProject()
    {
        if (SelectedProject == null) return;

        _logger.LogInformation("選擇載入專案: {Name}", SelectedProject.Name);

        // 設定為目前專案
        _projectConfigService.SetCurrentProject(SelectedProject);

        // 關閉視窗並返回成功
        RequestClose?.Invoke(true);
    }

    /// <summary>
    /// 取消並關閉視窗
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
