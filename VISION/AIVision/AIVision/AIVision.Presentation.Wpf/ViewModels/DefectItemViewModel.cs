using CommunityToolkit.Mvvm.ComponentModel;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class DefectItemViewModel : ObservableObject
{
    /// <summary>
    /// 建構子
    /// </summary>
    /// <param name="typeName">原始瑕疵類型名稱（用於比對，例如 "TF_crash"）</param>
    /// <param name="displayName">顯示名稱（用於 UI，例如 "TF碰撞"）</param>
    /// <param name="count">數量</param>
    /// <param name="total">總數</param>
    /// <param name="ratio">比例</param>
    /// <param name="trend">趨勢</param>
    public DefectItemViewModel(string typeName, string displayName, int count, int total, double ratio, string trend)
    {
        _typeName = typeName;
        _displayName = displayName;
        _count = count;
        _total = total;
        _ratio = ratio;
        _trend = trend;
    }

    /// <summary>
    /// 原始瑕疵類型名稱（用於比對，例如 "TF_crash"）
    /// </summary>
    public string TypeName
    {
        get => _typeName;
        private set => _typeName = value;
    }
    private string _typeName;

    /// <summary>
    /// 顯示名稱（用於 UI 綁定，例如 "TF碰撞"）
    /// </summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>
    /// Name 屬性保留相容性，回傳 DisplayName
    /// </summary>
    public string Name => DisplayName;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private double _ratio;

    [ObservableProperty]
    private string _trend;
}
