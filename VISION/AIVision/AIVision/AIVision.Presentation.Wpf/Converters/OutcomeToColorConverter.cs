using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AIVision.Presentation.Wpf.Converters;

/// <summary>
/// 模號三態核對結果字串 → 顯示色。
/// MixedAlarm = 紅(混料剔除)；Match / TrustInput = 綠(良)；Skip = 灰(略過)。
/// </summary>
public class OutcomeToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Red = new(System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F));
    private static readonly SolidColorBrush Green = new(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Grey = new(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var outcome = value as string;

        return outcome switch
        {
            "MixedAlarm" => Red,
            "Match" or "TrustInput" => Green,
            "Skip" => Grey,
            _ => Grey
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("OutcomeToColorConverter 僅支援單向綁定");
    }
}
