using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

/// <summary>Выбор файла скина (PNG) через системный диалог.</summary>
public interface ISkinFilePicker
{
    /// <summary>Путь к выбранному PNG или null, если пользователь отменил выбор.</summary>
    Task<string?> PickSkinAsync();
}

/// <summary>
/// Диалог выбора файла. Путь к скину — единственное, что возвращается вызывающему:
/// чтение и проверку PNG делает сервис профилей, поэтому здесь нет логики валидации.
/// Работает только из UI-потока: ViewModel не должен знать про TopLevel.
/// </summary>
public sealed class SkinFilePicker(ILogger<SkinFilePicker> logger) : ISkinFilePicker
{
    public Task<string?> PickSkinAsync() => Dispatcher.UIThread.InvokeAsync<string?>(async () =>
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null)
        {
            logger.LogDebug("Диалог выбора файла недоступен: окно ещё не создано");
            return null;
        }

        var storage = window.StorageProvider;
        if (!storage.CanOpen)
        {
            logger.LogDebug("Платформа не поддерживает выбор файлов");
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите скин Minecraft (PNG 64×64 или 64×32)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Скин Minecraft") { Patterns = ["*.png"], MimeTypes = ["image/png"] }
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        return file?.TryGetLocalPath();
    });
}
