using System.Windows;
using System.Windows.Media.Imaging;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

/// <summary>
/// OfflineTestView.xaml 的互動邏輯
/// </summary>
public partial class OfflineTestView : Window
{
    public OfflineTestView(OfflineTestViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 訂閱 CurrentImagePath 變更事件以載入影像
        viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(viewModel.CurrentImagePath))
            {
                LoadCurrentImage(viewModel.CurrentImagePath);
            }
        };
    }

    private void LoadCurrentImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            CurrentImage.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();

            CurrentImage.Source = bitmap;
        }
        catch
        {
            CurrentImage.Source = null;
        }
    }
}
