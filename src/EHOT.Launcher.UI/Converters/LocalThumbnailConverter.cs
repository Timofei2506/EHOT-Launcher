using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace EHOT.Launcher.UI.Converters;

/// <summary>Локальный путь изображения → Bitmap (кэш в пределах сессии, без перерисовки при скролле).</summary>
public sealed class LocalThumbnailConverter : IValueConverter
{
    public static LocalThumbnailConverter Instance { get; } = new();

    private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = culture;
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (_cache.TryGetValue(path, out var reference) && reference.TryGetTarget(out var cached))
        {
            return cached;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, parameter is string width && int.TryParse(width, out var w) ? w : 480, BitmapInterpolationMode.HighQuality);
            _cache[path] = new WeakReference<Bitmap>(bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
