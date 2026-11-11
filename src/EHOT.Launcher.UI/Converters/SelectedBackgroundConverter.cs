using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EHOT.Launcher.UI.Converters;

/// <summary>Выбран ли пункт навигации → фон и цвет текста.</summary>
public sealed class SelectedBackgroundConverter : IValueConverter
{
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#1F8B5CF6"));
    private static readonly IBrush Normal = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSelected = value is bool b && b;
        var isText = parameter is string p && p.Equals("text", StringComparison.OrdinalIgnoreCase);

        if (isText)
        {
            return isSelected
                ? new SolidColorBrush(Color.Parse("#F8FAFC"))
                : new SolidColorBrush(Color.Parse("#94A3B8"));
        }

        return isSelected ? Selected : Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
