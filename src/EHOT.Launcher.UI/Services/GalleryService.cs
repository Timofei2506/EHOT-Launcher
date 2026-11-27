using EHOT.Launcher.Core.Models;
using EHOT.Launcher.UI.ViewModels;

namespace EHOT.Launcher.UI.Services;

public sealed record GalleryItem(
    string FullPath,
    string FileName,
    string SourceLabel,
    DateTimeOffset CreatedAt,
    long SizeBytes)
{
    public string DateLabel => CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string SizeLabel => Converters.BytesToHumanConverter.Format(SizeBytes);
}

/// <summary>Сканирует реальные папки screenshots: общую и папки инстансов.</summary>
public interface IGalleryService
{
    Task<IReadOnlyList<GalleryItem>> ScanAsync(CancellationToken ct = default);
}

public sealed class GalleryService(IAppState state) : IGalleryService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    public Task<IReadOnlyList<GalleryItem>> ScanAsync(CancellationToken ct = default)
    {
        var result = new List<GalleryItem>();
        var root = state.MinecraftRoot;

        AddScreenshots(result, Path.Combine(root, "screenshots"), ".minecraft", ct);

        foreach (var profile in state.Profiles)
        {
            AddScreenshots(result, Path.Combine(profile.InstanceDirectory, "screenshots"), profile.Name, ct);
        }

        return Task.FromResult<IReadOnlyList<GalleryItem>>(
            result.OrderByDescending(i => i.CreatedAt).ToArray());
    }

    private static void AddScreenshots(List<GalleryItem> result, string directory, string source, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (!ImageExtensions.Contains(extension))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                result.Add(new GalleryItem(
                    info.FullName,
                    info.Name,
                    source,
                    info.LastWriteTimeUtc,
                    info.Length));
            }
            catch (IOException)
            {
                // Файл занят игрой — просто пропускаем до следующего обновления.
            }
            catch (UnauthorizedAccessException)
            {
                // Нет доступа к файлу — не роняем галерею.
            }
        }
    }
}
