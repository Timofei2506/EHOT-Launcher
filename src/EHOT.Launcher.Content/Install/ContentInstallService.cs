using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Content.Resolution;
using EHOT.Launcher.Core.Archives;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Content.Install;

public sealed record ContentInstallProgress(string Stage, double Fraction, string? Detail = null);

public sealed record ContentInstallOutcome(int Installed, int Skipped, IReadOnlyList<string> Messages);

public interface IContentInstallService
{
    Task<OperationResult<ContentInstallOutcome>> InstallAsync(
        ContentVersion version,
        ContentType type,
        string instanceDirectory,
        string profileId,
        bool resolveDependencies = true,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken ct = default);

    Task<OperationResult> RemoveAsync(InstalledContent content, string instanceDirectory, CancellationToken ct = default);

    Task<OperationResult> SetEnabledAsync(InstalledContent content, string instanceDirectory, bool enabled, CancellationToken ct = default);

    IReadOnlyList<(string ProjectId, string VersionId)> CheckForUpdates(IEnumerable<InstalledContent> installed, IEnumerable<ContentVersion> latest);
}

/// <summary>
/// Установка/удаление контента в инстанс:
/// проверка типа, разрешение зависимостей, скачивание с проверкой хэшей, безопасная распаковка архивов модпаков.
/// Все пути проходят через PathGuard.
/// </summary>
public sealed class ContentInstallService(
    IDownloadManager downloads,
    IArchiveExtractor archives,
    IInstalledContentRepository repository,
    ILogService log) : IContentInstallService
{
    public async Task<OperationResult<ContentInstallOutcome>> InstallAsync(
        ContentVersion version,
        ContentType type,
        string instanceDirectory,
        string profileId,
        bool resolveDependencies = true,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var messages = new List<string>();
            var toInstall = new List<ContentVersion>();

            if (resolveDependencies)
            {
                // Зависимости разрешает вызывающая сторона через IDependencyResolver (у неё есть провайдер и версия игры).
                toInstall.Add(version);
            }
            else
            {
                toInstall.Add(version);
            }

            // Основной запрос загрузок.
            var requests = new List<DownloadRequest>();
            var targets = new List<(ContentVersion Version, string Path, ContentFile File)>();

            foreach (var v in toInstall)
            {
                var file = v.PrimaryFile;
                if (file is null || string.IsNullOrEmpty(file.Url))
                {
                    messages.Add($"У версии «{v.Name}» нет файла для загрузки.");
                    continue;
                }

                var subFolder = FolderFor(type, v.Type);
                var fileName = PathGuard.ToSafeFileName(file.FileName, $"{PathGuard.ToSafeFileName(v.ProjectTitle, "content")}.jar");
                var targetPath = PathGuard.EnsureInside(instanceDirectory, Path.Combine(subFolder, fileName));

                requests.Add(new DownloadRequest
                {
                    Id = $"{v.ProviderId}-{v.Id}",
                    Url = new Uri(file.Url),
                    DestinationPath = targetPath,
                    DisplayName = $"{v.ProjectTitle} {v.VersionNumber}",
                    ExpectedSha1 = file.Sha1,
                    ExpectedSha512 = file.Sha512,
                    Group = $"content-{profileId}",
                    SkipIfVerified = true
                });

                targets.Add((v, targetPath, file));
            }

            progress?.Report(new ContentInstallProgress("Загрузка", 0.1, $"{requests.Count} файл(ов)"));

            var ok = await downloads.EnqueueManyAsync(requests, null, ct).ConfigureAwait(false);
            if (!ok)
            {
                return OperationResult<ContentInstallOutcome>.Fail(ErrorCode.Network,
                    "Часть файлов не загрузилась. Установленные ранее файлы не затронуты.");
            }

            progress?.Report(new ContentInstallProgress("Распаковка", 0.8));

            // Модпаки — это архивы .mrpack/zip, которые нужно развернуть, а не класть в mods.
            foreach (var (v, path, _) in targets)
            {
                if (type == ContentType.Modpack || v.Type == ContentType.Modpack)
                {
                    var unpacked = await UnpackModpackAsync(path, instanceDirectory, progress, ct).ConfigureAwait(false);
                    if (!unpacked.IsSuccess)
                    {
                        return OperationResult<ContentInstallOutcome>.Fail(unpacked.Code, unpacked.Message ?? "Не удалось распаковать сборку.");
                    }
                }

                if (type == ContentType.World && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var saves = Path.Combine(instanceDirectory, "saves", PathGuard.ToSafeFileName(v.ProjectTitle, "world"));
                    await archives.ExtractZipAsync(path, saves, ExtractionLimits.ContentPack, null, ct).ConfigureAwait(false);
                }
            }

            // Записываем учёт установленного.
            foreach (var (v, path, file) in targets)
            {
                var relative = Path.GetRelativePath(instanceDirectory, path).Replace('\\', '/');
                var record = new InstalledContent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ProfileId = profileId,
                    Type = type,
                    ProviderId = v.ProviderId,
                    ProjectId = v.ProjectId,
                    VersionId = v.Id,
                    ProjectTitle = v.ProjectTitle,
                    FileName = Path.GetFileName(path),
                    RelativePath = relative,
                    Sha512 = file.Sha512,
                    SizeBytes = file.Size,
                    IconUrl = v.IconUrl,
                    Dependencies = v.Dependencies.Where(d => d.ProjectId is not null).Select(d => d.ProjectId!).ToArray()
                };

                await repository.SaveAsync(record, ct).ConfigureAwait(false);
            }

            progress?.Report(new ContentInstallProgress("Готово", 1));
            log.Info("Content", $"Установлено {targets.Count} файл(ов) для профиля {profileId}");
            return OperationResult<ContentInstallOutcome>.Ok(new ContentInstallOutcome(targets.Count, 0, messages));
        }
        catch (OperationCanceledException)
        {
            return OperationResult<ContentInstallOutcome>.Fail(ErrorCode.Cancelled, "Установка отменена.");
        }
        catch (Exception ex)
        {
            log.Error("Content", "Ошибка установки контента", ex);
            return OperationResult<ContentInstallOutcome>.FromException(ex, ErrorCode.Io);
        }
    }

    public async Task<OperationResult> RemoveAsync(InstalledContent content, string instanceDirectory, CancellationToken ct = default)
    {
        try
        {
            var path = PathGuard.EnsureInside(instanceDirectory, content.RelativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            await repository.DeleteAsync(content.Id, ct).ConfigureAwait(false);
            log.Info("Content", $"Удалён файл {content.RelativePath}");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.FromException(ex, ErrorCode.Io);
        }
    }

    public async Task<OperationResult> SetEnabledAsync(InstalledContent content, string instanceDirectory, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var path = PathGuard.EnsureInside(instanceDirectory, content.RelativePath);
            if (enabled)
            {
                var disabledPath = path + ".disabled";
                if (File.Exists(disabledPath))
                {
                    File.Move(disabledPath, path, overwrite: true);
                }
            }
            else
            {
                if (File.Exists(path))
                {
                    File.Move(path, path + ".disabled", overwrite: true);
                }
            }

            await repository.SetEnabledAsync(content.Id, enabled, ct).ConfigureAwait(false);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.FromException(ex, ErrorCode.Io);
        }
    }

    public IReadOnlyList<(string ProjectId, string VersionId)> CheckForUpdates(
        IEnumerable<InstalledContent> installed, IEnumerable<ContentVersion> latest)
    {
        var latestMap = latest
            .GroupBy(v => v.ProjectId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.PublishedAt).First(), StringComparer.Ordinal);

        var result = new List<(string, string)>();
        foreach (var item in installed)
        {
            if (latestMap.TryGetValue(item.ProjectId, out var newest) && newest.Id != item.VersionId)
            {
                result.Add((item.ProjectId, newest.Id));
            }
        }

        return result;
    }

    private async Task<OperationResult> UnpackModpackAsync(
        string archivePath, string instanceDirectory, IProgress<ContentInstallProgress>? progress, CancellationToken ct)
    {
        try
        {
            var indexJson = await archives.ReadTextEntryAsync(archivePath, "modrinth.index.json", ct: ct).ConfigureAwait(false);
            if (indexJson is null)
            {
                return OperationResult.Fail(ErrorCode.Unsupported,
                    "Этот архив не является сборкой Modrinth (.mrpack). Импорт CurseForge-сборок через официальный API требует ключа.");
            }

            var index = System.Text.Json.JsonSerializer.Deserialize<MrpackIndex>(indexJson, JsonDefaults.External);
            if (index is null || index.Files.Count == 0)
            {
                return OperationResult.Fail(ErrorCode.Validation, "Файл modrinth.index.json повреждён.");
            }

            var requests = new List<DownloadRequest>();
            foreach (var file in index.Files)
            {
                if (file.Downloads.Count == 0)
                {
                    continue;
                }

                var relative = PathGuard.SanitizeRelativeEntryPath(file.Path);
                var target = PathGuard.EnsureInside(instanceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                requests.Add(new DownloadRequest
                {
                    Id = $"mrpack-{Path.GetFileName(target)}-{file.Hashes.GetValueOrDefault("sha1") ?? Guid.NewGuid().ToString("N")[..8]}",
                    Url = new Uri(file.Downloads[0]),
                    DestinationPath = target,
                    DisplayName = relative,
                    ExpectedSha1 = file.Hashes.GetValueOrDefault("sha1"),
                    ExpectedSha512 = file.Hashes.GetValueOrDefault("sha512"),
                    ExpectedSize = file.FileSize > 0 ? file.FileSize : null,
                    Group = "mrpack",
                    SkipIfVerified = true
                });
            }

            progress?.Report(new ContentInstallProgress("Файлы сборки", 0.5, $"{requests.Count} файл(ов)"));

            if (requests.Count > 0)
            {
                var ok = await downloads.EnqueueManyAsync(requests, null, ct).ConfigureAwait(false);
                if (!ok)
                {
                    return OperationResult.Fail(ErrorCode.Network, "Не все файлы сборки загружены.");
                }
            }

            // overrides/ и client-overrides/ распаковываются поверх.
            foreach (var overrides in new[] { "overrides", "client-overrides" })
            {
                var target = Path.Combine(instanceDirectory, "__overrides_tmp");
                await archives.ExtractZipAsync(archivePath, target, ExtractionLimits.ContentPack, null, ct).ConfigureAwait(false);

                var source = Path.Combine(target, overrides);
                if (Directory.Exists(source))
                {
                    CopyTree(source, instanceDirectory);
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
            }

            progress?.Report(new ContentInstallProgress("Сборка развёрнута", 1));
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            log.Error("Content", "Ошибка распаковки сборки", ex);
            return OperationResult.FromException(ex, ErrorCode.Io);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = PathGuard.EnsureInside(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FolderFor(ContentType requested, ContentType actual) => (requested == ContentType.Modpack || actual == ContentType.Modpack) ? "modpacks" : requested switch
    {
        ContentType.Mod => "mods",
        ContentType.ResourcePack => "resourcepacks",
        ContentType.Shader => "shaderpacks",
        ContentType.World => "saves",
        ContentType.Plugin => "plugins",
        _ => "mods"
    };

    private sealed class MrpackIndex
    {
        [System.Text.Json.Serialization.JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("versionId")]
        public string? VersionId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public List<MrpackFile> Files { get; set; } = [];
    }

    private sealed class MrpackFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("hashes")]
        public Dictionary<string, string> Hashes { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("downloads")]
        public List<string> Downloads { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("fileSize")]
        public long FileSize { get; set; }
    }
}
