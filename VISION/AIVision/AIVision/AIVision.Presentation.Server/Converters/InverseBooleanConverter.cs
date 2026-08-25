using System;
using System.Globalization;
using System.Windows.Data;

namespace AIVision.Presentation.Server.Converters;

/// <summary>bool 反向（按鈕在「檢查中」時要 disable）。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
