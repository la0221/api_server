using System.Globalization;
using System.Reflection;

namespace AIVision.Presentation.Wpf.Utilities;

public static class ObjectPathResolver
{
    public static object? Resolve(object source, string path)
    {
        if (source is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = source;

        foreach (var segment in segments)
        {
            if (current is null)
            {
                return null;
            }

            var type = current.GetType();
            var property = type.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null)
            {
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }

    public static string Format(object? value, string? format)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(format))
        {
            return DefaultFormat(value);
        }

        if (format.StartsWith("dt:", StringComparison.OrdinalIgnoreCase) && value is DateTime dt)
        {
            return dt.ToString(format[3..], CultureInfo.InvariantCulture);
        }

        if (format.StartsWith("ts:", StringComparison.OrdinalIgnoreCase) && value is TimeSpan ts)
        {
            return ts.ToString(format[3..], CultureInfo.InvariantCulture);
        }

        if (format.StartsWith("p", StringComparison.OrdinalIgnoreCase) && TryConvertDouble(value, out var pd))
        {
            return pd.ToString(format, CultureInfo.InvariantCulture);
        }

        if (format.StartsWith("n", StringComparison.OrdinalIgnoreCase) && TryConvertDouble(value, out var nd))
        {
            return nd.ToString(format, CultureInfo.InvariantCulture);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:" + format + "}", value);
    }

    private static string DefaultFormat(object value) =>
        value switch
        {
            DateTime dt => dt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture),
            double or float or decimal => Convert.ToDouble(value).ToString("G", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    private static bool TryConvertDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            default:
                if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    result = parsed;
                    return true;
                }

                result = 0;
                return false;
        }
    }
}
