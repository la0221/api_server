using System;
using System.Globalization;
using System.Windows.Data;

namespace AIVision.Presentation.Wpf.Converters;

public class CanGoNextPageConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is int currentPage && values[1] is int totalPages)
        {
            return currentPage < totalPages - 1;
        }

        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("CanGoNextPageConverter 僅支援單向綁定");
    }
}
