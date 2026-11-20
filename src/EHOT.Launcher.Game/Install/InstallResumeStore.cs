using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Game.Install;

/// <summary>Прерванная установка: что ставили и с каким загрузчиком.</summary>
public sealed record PendingInstall(
    string MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    string Directory,
    DateTimeOffset StartedAt);

/// <summary>
/// Помнит незавершённую установку версии. Нужна, чтобы после закрытия лаунчера
/// (или падения) при следующем запуске предложить продолжить, а не начинать с нуля:
/// сами файлы уже лежат на диске, недокачанное докачивается с места остановки.
/// </summary>
public interface IInstallResumeStore
{
    Task<PendingInstall?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(PendingInstall pending, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

public sealed class InstallResumeStore(IAppPaths paths, ILogService log) : IInstallResumeStore
{
    private string FilePath => Path.Combine(paths.AppDataDirectory, "pending-install.json");

    public async Task<PendingInstall?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize<PendingInstall>(json,
                Core.Net.JsonDefaults.Options);
        }
        catch (Exception ex)
        {
            log.Warn("Install", $"Не удалось прочитать состояние установки: {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(PendingInstall pending, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(pending, Core.Net.JsonDefaults.Options);
            await File.WriteAllTextAsync(FilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Не критично: в худшем случае установка начнётся заново.
            log.Warn("Install", $"Не удалось сохранить состояние установки: {ex.Message}");
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            log.Warn("Install", $"Не удалось очистить состояние установки: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
