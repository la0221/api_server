using System;
using System.Globalization;
using System.Windows.Data;

namespace AIVision.Presentation.Wpf.Converters;

public class PageIndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int pageIndex)
        {
            return pageIndex > 0;
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("PageIndexToBoolConverter 僅支援單向綁定");
    }
}
