using System;
using System.Globalization;
using System.Windows.Data;

namespace AIVision.Presentation.Wpf.Converters;

/// <summary>
/// 將布林值轉換為標題文字。
/// 如果 ConverterParameter 格式為 "TrueText|FalseText"，則根據布林值返回對應文字。
/// </summary>
public sealed class BooleanToTitleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string param)
        {
            return value;
        }

        var parts = param.Split('|');
        if (parts.Length != 2)
        {
            return value;
        }

        return boolValue ? parts[0] : parts[1];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("BooleanToTitleConverter 僅支援單向綁定");
    }
}

