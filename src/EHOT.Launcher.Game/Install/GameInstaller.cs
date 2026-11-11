using System.Text.Json;
using EHOT.Launcher.Core.Archives;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Minecraft;

namespace EHOT.Launcher.Game.Install;

public sealed record InstallProgress(string Stage, double Fraction, string? Detail = null);

public sealed record InstallOptions
{
    public bool InstallAssets { get; init; } = true;
    public bool InstallNatives { get; init; } = true;
    public bool AllowJavaDownload { get; init; } = true;
    public int JavaMajorOverride { get; init; }
}

public sealed record InstallResult(string VersionId, int FilesDownloaded, long BytesDownloaded);

public interface IGameInstaller
{
    Task<OperationResult<InstallResult>> InstallAsync(
        string minecraftDirectory,
        string versionId,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default);

    Task<bool> IsInstalledAsync(string minecraftDirectory, string versionId, CancellationToken ct = default);
}

/// <summary>
/// Установка версии Minecraft: version.json, client.jar, библиотеки, нативы, индекс ассетов и сами ассеты.
/// Все файлы проверяются по SHA-1 из официальных метаданных; запись атомарная (через менеджер загрузок).
/// </summary>
public sealed class GameInstaller(
    IMinecraftVersionService versions,
    IDownloadManager downloads,
    NativesExtractor natives,
    IPlatformInfo platform,
    ILogService log) : IGameInstaller
{
    private const string ResourcesUrl = "https://resources.download.minecraft.net";

    public async Task<bool> IsInstalledAsync(string minecraftDirectory, string versionId, CancellationToken ct = default)
    {
        var versionDir = Path.Combine(minecraftDirectory, "versions", versionId);
        if (!File.Exists(Path.Combine(versionDir, $"{versionId}.json")))
        {
            return false;
        }

        var resolved = await versions.ResolveAsync(versionId, Path.Combine(minecraftDirectory, "versions"), ct).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return false;
        }

        var clientJar = Path.Combine(versionDir, $"{versionId}.jar");
        if (!File.Exists(clientJar))
        {
            // У версий-лоадеров (Fabric/Forge) клиентский jar базовой версии лежит в своей папке.
            var baseJar = Path.Combine(minecraftDirectory, "versions", resolved.Value!.BaseVersionId, $"{resolved.Value.BaseVersionId}.jar");
            if (!File.Exists(baseJar))
            {
                return false;
            }
        }

        var nativesDir = Path.Combine(versionDir, "natives");
        return Directory.Exists(nativesDir) && Directory.EnumerateFiles(nativesDir).Any();
    }

    public async Task<OperationResult<InstallResult>> InstallAsync(
        string minecraftDirectory,
        string versionId,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new InstallOptions();

        try
        {
            var versionsDir = Path.Combine(minecraftDirectory, "versions");
            var versionDir = Path.Combine(versionsDir, versionId);
            Directory.CreateDirectory(versionDir);

            progress?.Report(new InstallProgress("Подготовка", 0, $"Версия {versionId}"));

            var resolvedResult = await versions.ResolveAsync(versionId, versionsDir, ct).ConfigureAwait(false);
            if (!resolvedResult.IsSuccess)
            {
                return OperationResult<InstallResult>.Fail(resolvedResult.Code, resolvedResult.Message ?? "Не удалось развернуть версию.");
            }

            var resolved = resolvedResult.Value!;

            // Гарантируем наличие version.json базовой версии на диске (для наследования).
            if (!string.Equals(resolved.BaseVersionId, versionId, StringComparison.OrdinalIgnoreCase))
            {
                var baseDir = Path.Combine(versionsDir, resolved.BaseVersionId);
                if (!File.Exists(Path.Combine(baseDir, $"{resolved.BaseVersionId}.json")))
                {
                    Directory.CreateDirectory(baseDir);
                    var baseJson = await versions.LoadVersionJsonAsync(resolved.BaseVersionId, versionsDir, ct).ConfigureAwait(false);
                    if (!baseJson.IsSuccess)
                    {
                        return OperationResult<InstallResult>.Fail(baseJson.Code, baseJson.Message ?? string.Empty);
                    }

                    await File.WriteAllTextAsync(
                        Path.Combine(baseDir, $"{resolved.BaseVersionId}.json"),
                        JsonSerializer.Serialize(baseJson.Value, JsonDefaults.External),
                        ct).ConfigureAwait(false);
                }
            }

            var requests = new List<DownloadRequest>();
            var step = 0.0;
            const double stepSize = 0.2;

            // 1. Клиентский JAR (только для базовой версии).
            var baseVersionDir = Path.Combine(versionsDir, resolved.BaseVersionId);
            Directory.CreateDirectory(baseVersionDir);
            if (resolved.Client?.Url is not null)
            {
                requests.Add(new DownloadRequest
                {
                    Id = $"client-{resolved.BaseVersionId}",
                    Url = new Uri(resolved.Client.Url),
                    DestinationPath = Path.Combine(baseVersionDir, $"{resolved.BaseVersionId}.jar"),
                    DisplayName = $"Клиент Minecraft {resolved.BaseVersionId}",
                    ExpectedSha1 = resolved.Client.Sha1,
                    ExpectedSize = resolved.Client.Size,
                    Group = $"install-{versionId}",
                    SkipIfVerified = true
                });
            }

            // 2. Библиотеки + нативы.
            var librariesDir = Path.Combine(minecraftDirectory, "libraries");
            var nativeJars = new List<(string Path, List<string> Exclude)>();
            foreach (var library in resolved.Libraries)
            {
                var coordinate = MavenCoordinate.Parse(library.Name);
                var artifact = library.Downloads?.Artifact;
                if (artifact?.Url is not null && artifact.Path is not null)
                {
                    requests.Add(new DownloadRequest
                    {
                        Id = $"lib-{coordinate.ToNotation()}-{artifact.Path}",
                        Url = new Uri(artifact.Url),
                        DestinationPath = Path.Combine(librariesDir, artifact.Path.Replace('/', Path.DirectorySeparatorChar)),
                        DisplayName = coordinate.ToNotation(),
                        ExpectedSha1 = artifact.Sha1,
                        ExpectedSize = artifact.Size,
                        Group = $"install-{versionId}",
                        SkipIfVerified = true
                    });
                }
                else if (library.BaseUrl is not null && library.Name.Contains(':') && artifact is null)
                {
                    // Лоадеры (Fabric/Quilt) публикуют библиотеки без downloads — собираем URL из maven-пути.
                    var path = coordinate.ToPath();
                    requests.Add(new DownloadRequest
                    {
                        Id = $"lib-{coordinate.ToNotation()}",
                        Url = new Uri(library.BaseUrl.TrimEnd('/') + "/" + path),
                        DestinationPath = Path.Combine(librariesDir, path.Replace('/', Path.DirectorySeparatorChar)),
                        DisplayName = coordinate.ToNotation(),
                        Group = $"install-{versionId}",
                        SkipIfVerified = false
                    });
                }

                // Нативы: имя классификатора зависит от ОС.
                if (library.Natives is not null)
                {
                    var osKey = platform.IsWindows ? "windows" : platform.IsMacOS ? "osx" : "linux";
                    if (library.Natives.TryGetValue(osKey, out var classifierTemplate))
                    {
                        var classifier = classifierTemplate.Replace("${arch}", platform.Architecture.Replace("x64", "64").Replace("arm64", "arm64"));
                        var nativeDl = library.Downloads?.Classifiers?.GetValueOrDefault(classifier);
                        if (nativeDl?.Url is not null && nativeDl.Path is not null)
                        {
                            var nativePath = Path.Combine(librariesDir, nativeDl.Path.Replace('/', Path.DirectorySeparatorChar));
                            requests.Add(new DownloadRequest
                            {
                                Id = $"native-{coordinate.ToNotation()}-{classifier}",
                                Url = new Uri(nativeDl.Url),
                                DestinationPath = nativePath,
                                DisplayName = $"{coordinate.Artifact}:{classifier}",
                                ExpectedSha1 = nativeDl.Sha1,
                                ExpectedSize = nativeDl.Size,
                                Group = $"install-{versionId}",
                                SkipIfVerified = true
                            });
                            nativeJars.Add((nativePath, library.Extract?.Exclude ?? []));
                        }
                        else if (library.Name.Contains(':') && library.BaseUrl is not null)
                        {
                            var full = MavenCoordinate.Parse(library.Name) with { Classifier = classifier };
                            var path = full.ToPath();
                            var nativePath = Path.Combine(librariesDir, path.Replace('/', Path.DirectorySeparatorChar));
                            requests.Add(new DownloadRequest
                            {
                                Id = $"native-{full.ToNotation()}",
                                Url = new Uri(library.BaseUrl.TrimEnd('/') + "/" + path),
                                DestinationPath = nativePath,
                                DisplayName = full.ToNotation(),
                                Group = $"install-{versionId}",
                                SkipIfVerified = false
                            });
                            nativeJars.Add((nativePath, library.Extract?.Exclude ?? []));
                        }
                    }
                }
            }

            progress?.Report(new InstallProgress("Загрузка библиотек", step += stepSize, $"{requests.Count} файлов"));

            var ok = await downloads.EnqueueManyAsync(requests, null, ct).ConfigureAwait(false);
            if (!ok)
            {
                return OperationResult<InstallResult>.Fail(ErrorCode.Io,
                    "Не все библиотеки удалось скачать. Проверьте соединение и повторите установку.");
            }

            // 3. Нативы.
            var nativesDir = Path.Combine(versionDir, "natives");
            if (options.InstallNatives && nativeJars.Count > 0)
            {
                progress?.Report(new InstallProgress("Распаковка нативов", step += stepSize));
                if (Directory.Exists(nativesDir))
                {
                    Directory.Delete(nativesDir, recursive: true);
                }

                Directory.CreateDirectory(nativesDir);
                foreach (var (path, exclude) in nativeJars)
                {
                    if (File.Exists(path))
                    {
                        await natives.ExtractAsync(path, nativesDir, exclude, ct).ConfigureAwait(false);
                    }
                }
            }

            // 4. Ассеты (индекс → объекты).
            if (options.InstallAssets && resolved.AssetIndex is not null)
            {
                progress?.Report(new InstallProgress("Индекс ассетов", step += stepSize));
                var assetsResult = await InstallAssetsAsync(minecraftDirectory, resolved, ct).ConfigureAwait(false);
                if (!assetsResult.IsSuccess)
                {
                    log.Warn("Install", "Ассеты установлены частично: " + assetsResult.Message);
                }
            }

            progress?.Report(new InstallProgress("Готово", 1));
            var totalBytes = requests.Sum(r => r.ExpectedSize ?? 0);
            await WriteInstallMarkerAsync(versionDir, resolved, ct).ConfigureAwait(false);

            log.Info("Install", $"Версия {versionId} установлена ({requests.Count} файлов)");
            return OperationResult<InstallResult>.Ok(new InstallResult(versionId, requests.Count, totalBytes));
        }
        catch (OperationCanceledException)
        {
            return OperationResult<InstallResult>.Fail(ErrorCode.Cancelled, "Установка отменена.");
        }
        catch (Exception ex)
        {
            log.Error("Install", $"Ошибка установки {versionId}", ex);
            return OperationResult<InstallResult>.FromException(ex, ErrorCode.Io);
        }
    }

    private async Task<OperationResult> InstallAssetsAsync(string minecraftDirectory, ResolvedVersion resolved, CancellationToken ct)
    {
        var assetsDir = Path.Combine(minecraftDirectory, "assets");
        var indexesDir = Path.Combine(assetsDir, "indexes");
        var objectsDir = Path.Combine(assetsDir, "objects");
        Directory.CreateDirectory(indexesDir);
        Directory.CreateDirectory(objectsDir);

        var indexRef = resolved.AssetIndex!;
        var indexPath = Path.Combine(indexesDir, $"{indexRef.Id}.json");

        if (!File.Exists(indexPath) || new FileInfo(indexPath).Length == 0)
        {
            var downloaded = await downloads.EnqueueAsync(new DownloadRequest
            {
                Id = $"asset-index-{indexRef.Id}",
                Url = new Uri(indexRef.Url),
                DestinationPath = indexPath,
                DisplayName = $"Индекс ассетов {indexRef.Id}",
                ExpectedSha1 = indexRef.Sha1,
                ExpectedSize = indexRef.Size > 0 ? indexRef.Size : null,
                Group = $"install-{resolved.Id}",
                SkipIfVerified = true
            }, ct).ConfigureAwait(false);

            if (!downloaded)
            {
                return OperationResult.Fail(ErrorCode.Network, "Не удалось скачать индекс ассетов.");
            }
        }

        var json = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
        var index = JsonSerializer.Deserialize<AssetIndexDto>(json, JsonDefaults.External);
        if (index is null)
        {
            return OperationResult.Fail(ErrorCode.Validation, "Индекс ассетов повреждён.");
        }

        // Виртуальные индексы (legacy) требуют копирования в assets/virtual/<id>.
        var requests = new List<DownloadRequest>(index.Objects.Count);
        foreach (var (name, obj) in index.Objects)
        {
            var sub = obj.Hash[..2];
            var dest = Path.Combine(objectsDir, sub, obj.Hash);
            if (File.Exists(dest))
            {
                continue;
            }

            requests.Add(new DownloadRequest
            {
                Id = $"asset-{obj.Hash}",
                Url = new Uri($"{ResourcesUrl}/{sub}/{obj.Hash}"),
                DestinationPath = dest,
                DisplayName = name,
                ExpectedSha1 = obj.Hash,
                ExpectedSize = obj.Size,
                Group = $"assets-{resolved.Id}",
                SkipIfVerified = true
            });
        }

        if (requests.Count > 0)
        {
            log.Info("Install", $"Загрузка {requests.Count} ассетов для {resolved.AssetsId}");
            var ok = await downloads.EnqueueManyAsync(requests, null, ct).ConfigureAwait(false);
            if (!ok)
            {
                return OperationResult.Fail(ErrorCode.Network, "Часть ассетов не загружена — игра запустится без некоторых звуков/языков.");
            }
        }

        if (index.Virtual == true && !string.IsNullOrEmpty(resolved.AssetsId))
        {
            var virtualDir = Path.Combine(assetsDir, "virtual", resolved.AssetsId);
            Directory.CreateDirectory(virtualDir);
            foreach (var (name, obj) in index.Objects)
            {
                var source = Path.Combine(objectsDir, obj.Hash[..2], obj.Hash);
                var target = Path.Combine(virtualDir, name.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(target);
                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                if (File.Exists(source) && !File.Exists(target))
                {
                    File.Copy(source, target);
                }
            }
        }

        return OperationResult.Ok();
    }

    /// <summary>Маркер установки: позволяет UI показать «установлено» без повторного резолва.</summary>
    private static async Task WriteInstallMarkerAsync(string versionDir, ResolvedVersion resolved, CancellationToken ct)
    {
        var marker = new
        {
            version = resolved.Id,
            baseVersion = resolved.BaseVersionId,
            installedAt = DateTimeOffset.UtcNow,
            javaMajor = resolved.JavaMajorVersion,
            mainClass = resolved.MainClass
        };

        await File.WriteAllTextAsync(
            Path.Combine(versionDir, ".ehot-installed.json"),
            JsonSerializer.Serialize(marker, JsonDefaults.Options),
            ct).ConfigureAwait(false);
    }
}
