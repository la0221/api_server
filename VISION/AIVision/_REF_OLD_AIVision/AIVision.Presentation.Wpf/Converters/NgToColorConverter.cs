using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AIVision.Presentation.Wpf.Converters;

public class NgToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isNg)
        {
            return isNg
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F))  // Red for NG
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // Green for OK
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("NgToColorConverter 僅支援單向綁定");
    }
}
