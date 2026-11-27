using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EHOT.Launcher.UI.Converters;

/// <summary>Выбран ли пункт навигации → фон, рамка и цвет текста.</summary>
public sealed class SelectedBackgroundConverter : IValueConverter
{
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#1AF07832"));
    private static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#40F07832"));
    private static readonly IBrush Normal = Brushes.Transparent;
    private static readonly IBrush NormalBorder = Brushes.Transparent;
    private static readonly IBrush SelectedText = new SolidColorBrush(Color.Parse("#F5F5F5"));
    private static readonly IBrush NormalText = new SolidColorBrush(Color.Parse("#999BA6"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSelected = value is bool b && b;
        var mode = parameter as string;

        if (string.Equals(mode, "text", StringComparison.OrdinalIgnoreCase))
        {
            return isSelected ? SelectedText : NormalText;
        }

        if (string.Equals(mode, "border", StringComparison.OrdinalIgnoreCase))
        {
            return isSelected ? SelectedBorder : NormalBorder;
        }

        return isSelected ? Selected : Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
