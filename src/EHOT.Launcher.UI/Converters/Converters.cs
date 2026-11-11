using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using EHOT.Launcher.UI.Services;

namespace EHOT.Launcher.UI.Converters;

/// <summary>Ключ иконки → геометрия из ресурсов приложения.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key)
        {
            return null;
        }

        if (Application.Current?.Resources.TryGetResource(key, null, out var resource) == true && resource is Geometry geometry)
        {
            return geometry;
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → видимость (с поддержкой инверсии параметром "invert").</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b;
}

/// <summary>ToastKind → цветовая метка.</summary>
public sealed class ToastKindBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value switch
        {
            ToastKind.Success => "#34D399",
            ToastKind.Warning => "#FBBF24",
            ToastKind.Error => "#F87171",
            _ => "#38BDF8"
        };

        return new SolidColorBrush(Color.Parse(color));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Дробное число 0..1 → процентная строка.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:0}%" : "0%";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Размер в байтах → человекочитаемый вид.</summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    public static string Format(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long l => Format(l),
            int i => Format(i),
            double d => Format((long)d),
            _ => "—"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Скорость в байтах/с.</summary>
public sealed class SpeedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && d > 0 ? BytesToHumanConverter.Format((long)d) + "/с" : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
