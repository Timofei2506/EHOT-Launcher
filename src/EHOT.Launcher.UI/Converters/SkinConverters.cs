using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EHOT.Launcher.UI.Converters;

/// <summary>
/// Путь к PNG-скину → <see cref="Bitmap"/> для показа на экране.
/// Используется в карточке аккаунта и на главном экране: если файла нет,
/// возвращается null, и разметка показывает инициал ника вместо пустого места.
/// </summary>
public sealed class SkinBitmapConverter : IValueConverter
{
    public static SkinBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception)
        {
            // Битый или недоступный файл — не роняем интерфейс, показываем инициал.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Скин 64×64 — это развёртка, а не портрет: для головы нужен кадр 8×8 из области лица.
/// Конвертер делает из скина маленькую голову (8×8 пикселей, увеличенную без сглаживания),
/// поэтому в списках аккаунтов и на главной видно настоящее лицо игрока, а не сплющенный PNG.
/// </summary>
public sealed class SkinFaceConverter : IValueConverter
{
    public static SkinFaceConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return SkinFaceRenderer.RenderFace(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Вырезает голову персонажа из PNG-скина (лицо 8×8 + слой шляпы).</summary>
public static class SkinFaceRenderer
{
    private const int FaceX = 8;
    private const int FaceY = 8;
    private const int HatX = 40;
    private const int HatY = 8;
    private const int Size = 8;

    public static Bitmap? RenderFace(string path)
    {
        using var source = new Bitmap(path);
        var scale = Math.Max(1, source.PixelSize.Width / 64);

        var face = CropPixels(source, FaceX * scale, FaceY * scale, Size * scale);
        var hat = source.PixelSize.Height >= 64
            ? CropPixels(source, HatX * scale, HatY * scale, Size * scale)
            : null;

        var composite = new byte[Size * scale * Size * scale * 4];
        for (var i = 0; i < composite.Length; i += 4)
        {
            var overlay = hat is not null && hat[i + 3] > 0;
            composite[i] = overlay ? hat![i] : face[i];
            composite[i + 1] = overlay ? hat![i + 1] : face[i + 1];
            composite[i + 2] = overlay ? hat![i + 2] : face[i + 2];
            composite[i + 3] = overlay ? hat![i + 3] : face[i + 3];
        }

        return CreateBitmap(composite, Size * scale);
    }

    private static byte[] CropPixels(Bitmap source, int x, int y, int size)
    {
        var buffer = new byte[size * size * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            source.CopyPixels(
                new PixelRect(x, y, size, size),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                size * 4);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    private static Bitmap CreateBitmap(byte[] pixels, int size)
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul,
                handle.AddrOfPinnedObject(),
                new PixelSize(size, size),
                new Vector(96, 96),
                size * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>
/// Официальная текстура скина Modrinth/CDN доступна только по http(s), но картинки
/// в интерфейсе грузятся асинхронно: пока иконка не пришла, показываем инициал.
/// Конвертер просто проверяет, что ссылка выглядит как абсолютный адрес.
/// </summary>
public sealed class IconUrlConverter : IValueConverter
{
    public static IconUrlConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
