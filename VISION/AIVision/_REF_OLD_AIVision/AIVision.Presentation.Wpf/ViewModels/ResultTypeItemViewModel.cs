using CommunityToolkit.Mvvm.ComponentModel;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 結果類型項目的 ViewModel（用於模型編輯界面）。
/// </summary>
public partial class ResultTypeItemViewModel : ObservableObject
{
    /// <summary>
    /// 結果類型名稱（用於系統識別，如 "scratch", "stain"）
    /// </summary>
    [ObservableProperty]
    private string typeName = string.Empty;

    /// <summary>
    /// 顯示名稱（用於 UI 顯示，如 "刮痕", "髒汙"）
    /// </summary>
    [ObservableProperty]
    private string displayName = string.Empty;

    public ResultTypeItemViewModel()
    {
    }

    public ResultTypeItemViewModel(string typeName, string displayName)
    {
        TypeName = typeName;
        DisplayName = displayName;
    }
}
