using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>
/// 離線(本地 ONNX)模型「編輯模型」對話框。編輯單一模型旁置設定 &lt;name&gt;.config.json。
/// 純本地,不涉及 AINAVI;與 AINAVI 形狀的 ModelEditView 無關。
/// </summary>
public partial class OfflineModelEditView : Window
{
    public OfflineModelEditView(OfflineModelEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += (_, result) =>
        {
            DialogResult = result;
            Close();
        };
    }
}
