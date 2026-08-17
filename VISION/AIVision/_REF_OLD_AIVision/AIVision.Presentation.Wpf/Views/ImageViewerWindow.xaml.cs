using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AIVision.Presentation.Wpf.ViewModels;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfScaleTransform = System.Windows.Media.ScaleTransform;

namespace AIVision.Presentation.Wpf.Views;

public partial class ImageViewerWindow : Window
{
    private readonly IList<InspectionHistoryItemViewModel> _items;
    private int _currentIndex;
    private double _zoomLevel = 1.0;

    public ImageViewerWindow(
        IList<InspectionHistoryItemViewModel> items,
        int initialIndex)
    {
        InitializeComponent();

        _items = items;
        _currentIndex = initialIndex;

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
        {
            return;
        }

        var item = _items[_currentIndex];

        // 載入圖片（優先顯示標註圖）
        var imagePath = item.IsNg && !string.IsNullOrEmpty(item.AnnotatedImagePath)
            ? item.AnnotatedImagePath
            : item.ImagePath;

        if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                MainImage.Source = bitmap;
                _zoomLevel = 1.0;
            }
            catch
            {
                MainImage.Source = null;
            }
        }
        else
        {
            MainImage.Source = null;
        }

        // 更新資訊
        WorkOrderText.Text = item.WorkOrderCode;
        TimestampText.Text = item.Timestamp;
        ConfidenceText.Text = item.ConfidenceText;
        DefectCountText.Text = item.DefectCount.ToString();
        DefectCountText.Foreground = item.IsNg
            ? new WpfBrush(WpfColor.FromRgb(0xF4, 0x43, 0x36))
            : new WpfBrush(WpfColors.White);

        // 更新結果標籤
        var isNg = item.IsNg;
        ResultBadge.Background = isNg
            ? new WpfBrush(WpfColor.FromRgb(0xD3, 0x2F, 0x2F))
            : new WpfBrush(WpfColor.FromRgb(0x4C, 0xAF, 0x50));
        ResultText.Text = isNg ? "NG" : "OK";

        // 更新頁碼
        PageIndexText.Text = (_currentIndex + 1).ToString();
        TotalCountText.Text = _items.Count.ToString();

        // 更新按鈕狀態
        PrevButton.IsEnabled = _currentIndex > 0;
        NextButton.IsEnabled = _currentIndex < _items.Count - 1;

        // 更新標題
        Title = $"圖片檢視 - {item.WorkOrderCode} ({_currentIndex + 1}/{_items.Count})";
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        NavigatePrevious();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateNext();
    }

    private void NavigatePrevious()
    {
        if (_currentIndex > 0)
        {
            _currentIndex--;
            UpdateDisplay();
        }
    }

    private void NavigateNext()
    {
        if (_currentIndex < _items.Count - 1)
        {
            _currentIndex++;
            UpdateDisplay();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
                NavigatePrevious();
                e.Handled = true;
                break;

            case Key.Right:
            case Key.Down:
                NavigateNext();
                e.Handled = true;
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Home:
                if (_items.Count > 0)
                {
                    _currentIndex = 0;
                    UpdateDisplay();
                }
                e.Handled = true;
                break;

            case Key.End:
                if (_items.Count > 0)
                {
                    _currentIndex = _items.Count - 1;
                    UpdateDisplay();
                }
                e.Handled = true;
                break;
        }
    }

    private void MainImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 滾輪縮放
        if (e.Delta > 0)
        {
            _zoomLevel = Math.Min(_zoomLevel * 1.1, 5.0);
        }
        else
        {
            _zoomLevel = Math.Max(_zoomLevel / 1.1, 0.2);
        }

        MainImage.LayoutTransform = new WpfScaleTransform(_zoomLevel, _zoomLevel);
        e.Handled = true;
    }
}
