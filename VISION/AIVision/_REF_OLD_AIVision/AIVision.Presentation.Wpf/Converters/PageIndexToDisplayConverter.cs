using System;
using System.Globalization;
using System.Windows.Data;

namespace AIVision.Presentation.Wpf.Converters;

public class PageIndexToDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int pageIndex)
        {
            return pageIndex + 1;
        }

        return 1;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("PageIndexToDisplayConverter 僅支援單向綁定");
    }
}
