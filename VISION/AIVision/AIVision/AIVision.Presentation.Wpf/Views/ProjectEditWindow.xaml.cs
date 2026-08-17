using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>
/// 專案編輯視窗
/// </summary>
public partial class ProjectEditWindow : Window
{
    public ProjectEditWindow(ProjectEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 訂閱 ViewModel 的關閉請求
        viewModel.RequestClose += OnRequestClose;

        Closed += (_, _) => viewModel.RequestClose -= OnRequestClose;
    }

    private void OnRequestClose(bool dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
