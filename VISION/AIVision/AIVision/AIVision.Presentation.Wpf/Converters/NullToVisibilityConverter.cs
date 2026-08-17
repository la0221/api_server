using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AIVision.Presentation.Wpf.Converters;

/// <summary>
/// Null 轉換為 Visibility（非空 -> Visible，空 -> Collapsed）
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return string.IsNullOrWhiteSpace(str) ? Visibility.Collapsed : Visibility.Visible;
        }
        return value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("此轉換器僅支援單向綁定");
    }
}

/// <summary>
/// Null 轉換為 Visibility（反向：空 -> Visible，非空 -> Collapsed）
/// 用於「無圖片」等佔位符顯示
/// </summary>
public sealed class NullToVisibilityInverseConverter : IValueConverter
{
    public static readonly NullToVisibilityInverseConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return string.IsNullOrWhiteSpace(str) ? Visibility.Visible : Visibility.Collapsed;
        }
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("此轉換器僅支援單向綁定");
    }
}

