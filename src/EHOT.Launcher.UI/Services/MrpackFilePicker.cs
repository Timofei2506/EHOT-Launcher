using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.UI.Services;

/// <summary>Выбор .mrpack через системный диалог.</summary>
public interface IMrpackFilePicker
{
    Task<string?> PickMrpackAsync();
}

/// <summary>См. <see cref="SkinFilePicker"/> — тот же паттерн, только для сборок Modrinth (.mrpack).</summary>
public sealed class MrpackFilePicker(ILogger<MrpackFilePicker> logger) : IMrpackFilePicker
{
    public Task<string?> PickMrpackAsync() => Dispatcher.UIThread.InvokeAsync<string?>(async () =>
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
            Title = "Выберите сборку Minecraft (.mrpack)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Сборка Modrinth") { Patterns = ["*.mrpack"] }
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        return file?.TryGetLocalPath();
    });
}
