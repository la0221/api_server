using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;
using WpfImage = System.Windows.Controls.Image;

namespace AIVision.Presentation.Wpf.Views;

public partial class LineScanView : Window
{
    public LineScanView(LineScanViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void PreviewImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is LineScanViewModel vm && sender is WpfImage image)
        {
            var position = e.GetPosition(image);
            var controlSize = new System.Windows.Size(image.ActualWidth, image.ActualHeight);

            // 只有當影像存在且控件有實際大小時才處理
            if (controlSize.Width > 0 && controlSize.Height > 0)
            {
                vm.OnImageMouseMove(position, controlSize);
            }
        }
    }

    private void PreviewImage_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is LineScanViewModel vm)
        {
            vm.OnImageMouseLeave();
        }
    }
}
