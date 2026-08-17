using System;
using System.Globalization;
using System.Windows.Data;
using AIVision.Presentation.Wpf.Models;

namespace AIVision.Presentation.Wpf.Converters;

/// <summary>
/// 將 ModelType enum 轉換為顯示文字。
/// </summary>
public sealed class ModelTypeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ModelType modelType)
        {
            return modelType switch
            {
                ModelType.Classification => "分類",
                ModelType.Detection => "檢測",
                _ => value.ToString()
            };
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return str switch
            {
                "分類" => ModelType.Classification,
                "檢測" => ModelType.Detection,
                _ => ModelType.Classification
            };
        }
        return ModelType.Classification;
    }
}

