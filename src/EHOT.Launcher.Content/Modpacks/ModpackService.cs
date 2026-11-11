using System.IO.Compression;
using System.Text.Json;
using EHOT.Launcher.Content.Install;
using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Core.Archives;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Storage;

namespace EHOT.Launcher.Content.Modpacks;

public sealed record ModpackSummary(
    string Id,
    string Name,
    string Version,
    string MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    string Source,
    string? ProjectId,
    string? InstalledProfileId,
    DateTimeOffset ImportedAt,
    int Mods);

public sealed record ModpackBackup(string Path, DateTimeOffset CreatedAt, long SizeBytes);

public interface IModpackService
{
    Task<OperationResult<ModpackSummary>> ImportMrpackAsync(
        string archivePath,
        string targetRoot,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Экспорт профиля в .mrpack.</summary>
    Task<OperationResult<string>> ExportMrpackAsync(
        GameProfile profile,
        string outputPath,
        CancellationToken ct = default);

    Task<OperationResult<ModpackBackup>> BackupAsync(GameProfile profile, CancellationToken ct = default);

    Task<OperationResult> RestoreAsync(GameProfile profile, string backupPath, CancellationToken ct = default);
}

/// <summary>
/// Менеджер сборок: импорт .mrpack, экспорт профиля в .mrpack, резервные копии и восстановление.
/// </summary>
public sealed class ModpackService(
    IArchiveExtractor archives,
    IDownloadManager downloads,
    IGameInstallerBridge installer,
    IProfileRepository profiles,
    IInstalledContentRepository content,
    ILogService log) : IModpackService
{
    public async Task<OperationResult<ModpackSummary>> ImportMrpackAsync(
        string archivePath,
        string targetRoot,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new ContentInstallProgress("Чтение индекса", 0.05));
            var indexJson = await archives.ReadTextEntryAsync(archivePath, "modrinth.index.json", ct: ct).ConfigureAwait(false);
            if (indexJson is null)
            {
                return OperationResult<ModpackSummary>.Fail(ErrorCode.Unsupported,
                    "Файл не является сборкой Modrinth (.mrpack): отсутствует modrinth.index.json.");
            }

            var index = JsonSerializer.Deserialize<MrpackIndex>(indexJson, JsonDefaults.External);
            if (index is null)
            {
                return OperationResult<ModpackSummary>.Fail(ErrorCode.Validation, "Индекс сборки повреждён.");
            }

            var name = index.Name ?? Path.GetFileNameWithoutExtension(archivePath);
            var mcVersion = index.Dependencies.GetValueOrDefault("minecraft") ?? "1.21.1";
            var loader = DetectLoader(index.Dependencies, out var loaderVersion);

            progress?.Report(new ContentInstallProgress("Создание профиля", 0.15, $"{name} · {mcVersion} · {loader}"));

            var profileId = Guid.NewGuid().ToString("N")[..12];
            var dirName = PathGuard.ToSafeFileName($"{name}-{profileId}", $"pack-{profileId}");
            var instanceDir = Path.Combine(targetRoot, "instances", dirName);
            Directory.CreateDirectory(instanceDir);
            foreach (var sub in new[] { "mods", "resourcepacks", "shaderpacks", "saves", "game", "logs", "versions", "libraries", "assets" })
            {
                Directory.CreateDirectory(Path.Combine(instanceDir, sub));
            }

            // 1. Скачиваем файлы из индекса.
            var requests = new List<DownloadRequest>();
            foreach (var file in index.Files)
            {
                if (file.Downloads.Count == 0)
                {
                    continue;
                }

                var relative = PathGuard.SanitizeRelativeEntryPath(file.Path);
                var target = PathGuard.EnsureInside(instanceDir, relative.Replace('/', Path.DirectorySeparatorChar));
                requests.Add(new DownloadRequest
                {
                    Id = $"mrpack-{profileId}-{Path.GetFileName(target)}-{file.Hashes.GetValueOrDefault("sha1") ?? Guid.NewGuid().ToString("N")[..8]}",
                    Url = new Uri(file.Downloads[0]),
                    DestinationPath = target,
                    DisplayName = relative,
                    ExpectedSha1 = file.Hashes.GetValueOrDefault("sha1"),
                    ExpectedSha512 = file.Hashes.GetValueOrDefault("sha512"),
                    ExpectedSize = file.FileSize > 0 ? file.FileSize : null,
                    Group = $"mrpack-{profileId}",
                    SkipIfVerified = true
                });
            }

            progress?.Report(new ContentInstallProgress("Загрузка модов", 0.3, $"{requests.Count} файл(ов)"));
            if (requests.Count > 0)
            {
                var ok = await downloads.EnqueueManyAsync(requests, null, ct).ConfigureAwait(false);
                if (!ok)
                {
                    return OperationResult<ModpackSummary>.Fail(ErrorCode.Network, "Не все файлы сборки загружены. Попробуйте снова.");
                }
            }

            // 2. Overrides.
            progress?.Report(new ContentInstallProgress("Распаковка overrides", 0.8));
            var temp = Path.Combine(instanceDir, "__overrides");
            await archives.ExtractZipAsync(archivePath, temp, ExtractionLimits.ContentPack, null, ct).ConfigureAwait(false);
            foreach (var folder in new[] { "overrides", "client-overrides" })
            {
                var source = Path.Combine(temp, folder);
                if (Directory.Exists(source))
                {
                    CopyTree(source, instanceDir);
                }
            }

            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            // 3. Профиль и установка версии игры/лоадера.
            var profile = new GameProfile
            {
                Id = profileId,
                Name = name,
                MinecraftVersion = mcVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                InstanceDirectory = instanceDir,
                AllocatedMemoryMb = 4096,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await profiles.SaveAsync(profile, ct).ConfigureAwait(false);

            progress?.Report(new ContentInstallProgress("Установка версии игры", 0.85));
            var install = await installer.InstallVersionAsync(instanceDir, mcVersion, loader, loaderVersion, null, ct).ConfigureAwait(false);
            if (!install.IsSuccess)
            {
                log.Warn("Modpack", "Сборка импортирована, но версия игры не установлена: " + install.Message);
            }

            var modsCount = Directory.Exists(Path.Combine(instanceDir, "mods"))
                ? Directory.EnumerateFiles(Path.Combine(instanceDir, "mods"), "*.jar").Count()
                : 0;

            var summary = new ModpackSummary(
                profileId, name, index.VersionId ?? "1.0.0", mcVersion, loader, loaderVersion,
                "modrinth", null, profileId, DateTimeOffset.UtcNow, modsCount);

            progress?.Report(new ContentInstallProgress("Готово", 1));
            log.Info("Modpack", $"Импортирована сборка «{name}» ({modsCount} модов)");
            return OperationResult<ModpackSummary>.Ok(summary);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<ModpackSummary>.Fail(ErrorCode.Cancelled, "Импорт отменён.");
        }
        catch (Exception ex)
        {
            log.Error("Modpack", "Ошибка импорта сборки", ex);
            return OperationResult<ModpackSummary>.FromException(ex, ErrorCode.Io);
        }
    }

    public async Task<OperationResult<string>> ExportMrpackAsync(GameProfile profile, string outputPath, CancellationToken ct = default)
    {
        try
        {
            var modsDir = Path.Combine(profile.InstanceDirectory, "mods");
            var installed = await content.GetForProfileAsync(profile.Id, ct).ConfigureAwait(false);
            var index = new
            {
                formatVersion = 1,
                game = "minecraft",
                versionId = "1.0.0",
                name = profile.Name,
                summary = $"Сборка EHOT Launcher: {profile.Name}",
                files = installed
                    .Where(i => i.Type == ContentType.Mod && i.ProviderId == "modrinth")
                    .Select(i => new
                    {
                        path = i.RelativePath,
                        hashes = new Dictionary<string, string> { ["sha1"] = i.Sha512 ?? string.Empty },
                        env = new { client = "required", server = "required" },
                        downloads = Array.Empty<string>(),
                        fileSize = i.SizeBytes
                    }).ToArray(),
                dependencies = profile.Loader switch
                {
                    LoaderKind.Fabric => new Dictionary<string, string>
                    {
                        ["minecraft"] = profile.MinecraftVersion,
                        ["fabric-loader"] = profile.LoaderVersion ?? string.Empty
                    },
                    LoaderKind.Quilt => new Dictionary<string, string>
                    {
                        ["minecraft"] = profile.MinecraftVersion,
                        ["quilt-loader"] = profile.LoaderVersion ?? string.Empty
                    },
                    LoaderKind.Forge => new Dictionary<string, string>
                    {
                        ["minecraft"] = profile.MinecraftVersion,
                        ["forge"] = profile.LoaderVersion ?? string.Empty
                    },
                    LoaderKind.NeoForge => new Dictionary<string, string>
                    {
                        ["minecraft"] = profile.MinecraftVersion,
                        ["neoforge"] = profile.LoaderVersion ?? string.Empty
                    },
                    _ => new Dictionary<string, string> { ["minecraft"] = profile.MinecraftVersion }
                }
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            await using (var es = entry.Open())
            await using (var writer = new StreamWriter(es))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(index, JsonDefaults.External)).ConfigureAwait(false);
            }

            if (Directory.Exists(modsDir))
            {
                foreach (var file in Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = "overrides/" + Path.GetRelativePath(profile.InstanceDirectory, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
            }

            log.Info("Modpack", $"Экспортирована сборка «{profile.Name}» → {outputPath}");
            return OperationResult<string>.Ok(outputPath);
        }
        catch (Exception ex)
        {
            return OperationResult<string>.FromException(ex, ErrorCode.Io);
        }
    }

    public async Task<OperationResult<ModpackBackup>> BackupAsync(GameProfile profile, CancellationToken ct = default)
    {
        try
        {
            var backupDir = Path.Combine(profile.InstanceDirectory, ".backups");
            Directory.CreateDirectory(backupDir);
            var path = Path.Combine(backupDir, $"backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var folder in new[] { "mods", "config", "resourcepacks", "shaderpacks", "game" })
                {
                    var dir = Path.Combine(profile.InstanceDirectory, folder);
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(profile.InstanceDirectory, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                    }
                }
            }, ct).ConfigureAwait(false);

            var info = new FileInfo(path);
            log.Info("Modpack", $"Создана резервная копия {path} ({info.Length} байт)");
            return OperationResult<ModpackBackup>.Ok(new ModpackBackup(path, DateTimeOffset.Now, info.Length));
        }
        catch (Exception ex)
        {
            return OperationResult<ModpackBackup>.FromException(ex, ErrorCode.Io);
        }
    }

    public async Task<OperationResult> RestoreAsync(GameProfile profile, string backupPath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                return OperationResult.Fail(ErrorCode.NotFound, "Резервная копия не найдена.");
            }

            await archives.ExtractZipAsync(backupPath, profile.InstanceDirectory, ExtractionLimits.ContentPack, null, ct).ConfigureAwait(false);
            log.Info("Modpack", $"Восстановлено из {backupPath}");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.FromException(ex, ErrorCode.Io);
        }
    }

    private static LoaderKind DetectLoader(Dictionary<string, string> dependencies, out string? loaderVersion)
    {
        loaderVersion = null;
        if (dependencies.TryGetValue("fabric-loader", out var fabric))
        {
            loaderVersion = fabric;
            return LoaderKind.Fabric;
        }

        if (dependencies.TryGetValue("quilt-loader", out var quilt))
        {
            loaderVersion = quilt;
            return LoaderKind.Quilt;
        }

        if (dependencies.TryGetValue("forge", out var forge))
        {
            loaderVersion = forge;
            return LoaderKind.Forge;
        }

        if (dependencies.TryGetValue("neoforge", out var neo))
        {
            loaderVersion = neo;
            return LoaderKind.NeoForge;
        }

        return LoaderKind.Vanilla;
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

    private sealed class MrpackIndex
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("versionId")]
        public string? VersionId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public List<MrpackFile> Files { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("dependencies")]
        public Dictionary<string, string> Dependencies { get; set; } = [];
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

/// <summary>Мост к игровому модулю, чтобы Content не зависел от Game напрямую.</summary>
public interface IGameInstallerBridge
{
    Task<OperationResult> InstallVersionAsync(
        string instanceDirectory,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        IProgress<InstallProgressBridge>? progress,
        CancellationToken ct = default);
}

public sealed record InstallProgressBridge(string Stage, double Fraction, string? Detail = null);
