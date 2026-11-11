using System.Globalization;
using Avalonia.Data.Converters;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>
/// Мелкие конвертеры для UI. Осознанно держим их в ViewModels-пространстве имён,
/// чтобы разметка не тянула бизнес-логику.
/// </summary>
public static class TextConverters
{
    public static IValueConverter FirstLetter { get; } = new FuncValueConverter<string?, string>(
        value => string.IsNullOrEmpty(value) ? "?" : value[..1].ToUpperInvariant());

    public static IValueConverter IsZero { get; } = new FuncValueConverter<int, bool>(count => count == 0);

    public static IValueConverter IsPositive { get; } = new FuncValueConverter<int, bool>(count => count > 0);

    public static IValueConverter ShortDate { get; } = new FuncValueConverter<DateTimeOffset, string>(
        value => value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));

    public static IValueConverter MbLabel { get; } = new FuncValueConverter<int, string>(mb => $"{mb} МБ");

    public static IValueConverter JoinList { get; } = new FuncValueConverter<System.Collections.IEnumerable?, string>(
        value =>
        {
            if (value is null)
            {
                return "—";
            }

            var items = value.Cast<object?>().Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Take(4);
            var text = string.Join(", ", items);
            return string.IsNullOrEmpty(text) ? "—" : text;
        });
}
