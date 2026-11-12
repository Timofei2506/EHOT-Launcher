using System.Collections.Concurrent;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace EHOT.Launcher.UI.Controls;

/// <summary>
/// Картинка по сетевому адресу (иконки модов Modrinth).
/// <para>
/// Avalonia сама умеет грузить только локальные файлы, поэтому иконка скачивается в фоне:
/// пока ответа нет, контрол пустой и разметка показывает инициал названия — интерфейс
/// не «прыгает» и не висит. Уже полученные иконки кэшируются в памяти, повторные
/// карточки не делают новых запросов. Если сеть недоступна, остаётся инициал.
/// </para>
/// </summary>
public sealed class RemoteImage : Image
{
    private const int CacheLimit = 300;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));

    private CancellationTokenSource? _pending;

    static RemoteImage()
    {
        UrlProperty.Changed.AddClassHandler<RemoteImage>((image, args) => image.OnUrlChanged(args.NewValue as string));
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private void OnUrlChanged(string? url)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            Source = null;
            return;
        }

        if (Cache.TryGetValue(url, out var cached))
        {
            Source = cached;
            return;
        }

        Source = null;
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = LoadAsync(url, cts.Token);
    }

    private async Task LoadAsync(string url, CancellationToken ct)
    {
        Bitmap? bitmap = null;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes, writable: false);
            bitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
            // Иконка не критична: остаётся инициал, ошибка не всплывает в интерфейс.
            return;
        }

        if (ct.IsCancellationRequested)
        {
            bitmap.Dispose();
            return;
        }

        if (Cache.Count < CacheLimit)
        {
            Cache.TryAdd(url, bitmap);
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ct.IsCancellationRequested && Url == url)
            {
                Source = bitmap;
            }
        });
    }
}
