using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

/// <summary>
/// Буфер обмена. ViewModel не должен лезть в TopLevel напрямую, поэтому копирование
/// инкапсулировано здесь: один метод, честный результат (получилось / нет), работа из UI-потока.
/// </summary>
public interface IClipboardService
{
    /// <summary>Кладёт текст в системный буфер обмена. false — если буфер недоступен.</summary>
    Task<bool> SetTextAsync(string text);

    /// <summary>Читает текст из буфера обмена (null — пусто или недоступно).</summary>
    Task<string?> GetTextAsync();
}

public sealed class ClipboardService(ILogger<ClipboardService> logger) : IClipboardService
{
    public Task<bool> SetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(false);
        }

        return Dispatcher.UIThread.InvokeAsync<bool>(async () =>
        {
            try
            {
                var clipboard = await GetClipboardAsync();
                if (clipboard is null)
                {
                    logger.LogDebug("Буфер обмена недоступен в этом окружении");
                    return false;
                }

                await clipboard.SetTextAsync(text);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Копирование не удалось: {Message}", ex.Message);
                return false;
            }
        });
    }

    public Task<string?> GetTextAsync()
        => Dispatcher.UIThread.InvokeAsync<string?>(async () =>
        {
            try
            {
                var clipboard = await GetClipboardAsync();
                if (clipboard is null)
                {
                    return null;
                }

                // В Avalonia 12 чтение текста — через TryGetTextAsync (не бросает исключение на пустом буфере).
                return await clipboard.TryGetTextAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug("Чтение буфера обмена не удалось: {Message}", ex.Message);
                return null;
            }
        });

    private static async Task<IClipboard?> GetClipboardAsync()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is not null)
        {
            return window.Clipboard;
        }

        // Фолбэк для headless-тестов и ранних вызовов: TopLevel ещё может быть не готов.
        return await Task.FromResult<IClipboard?>(null);
    }
}
