using System.Text.Json;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Game.Minecraft;
using EHOT.Launcher.Game.Runtimes;

namespace EHOT.Launcher.Game.Install;

public sealed record LoaderVersionOption(string Version, string MinecraftVersion, string? Stable = null);

public interface ILoaderInstaller
{
    Task<OperationResult<IReadOnlyList<LoaderVersionOption>>> GetLoaderVersionsAsync(
        LoaderKind loader, string minecraftVersion, CancellationToken ct = default);

    /// <summary>Устанавливает лоадер и возвращает id установленной версии для запуска.</summary>
    Task<OperationResult<string>> InstallLoaderAsync(
        string minecraftDirectory,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Установка мод-лоадеров:
/// Fabric/Quilt — метаданные и version.json напрямую (без запуска чужих установщиков);
/// Forge/NeoForge — официальный installer JAR (единственный поддерживаемый способ) с проверкой кода возврата.
/// </summary>
public sealed class LoaderInstaller(
    IHttpService http,
    IDownloadManager downloads,
    IGameInstaller gameInstaller,
    IProcessRunner runner,
    JavaRuntimeManager javaManager,
    ILogService log) : ILoaderInstaller
{
    private static readonly Uri FabricMeta = new("https://meta.fabricmc.net/v2/versions/loader");
    private static readonly Uri FabricLoaderForMc = new("https://meta.fabricmc.net/v2/versions/loader/{0}");
    private static readonly Uri QuiltMeta = new("https://meta.quiltmc.org/v3/versions/loader");
    private static readonly Uri QuiltLoaderForMc = new("https://meta.quiltmc.org/v3/versions/loader/{0}");

    public async Task<OperationResult<IReadOnlyList<LoaderVersionOption>>> GetLoaderVersionsAsync(
        LoaderKind loader, string minecraftVersion, CancellationToken ct = default)
    {
        try
        {
            return loader switch
            {
                LoaderKind.Fabric => await GetFabricLikeAsync(FabricLoaderForMc, minecraftVersion, ct).ConfigureAwait(false),
                LoaderKind.Quilt => await GetFabricLikeAsync(QuiltLoaderForMc, minecraftVersion, ct).ConfigureAwait(false),
                LoaderKind.Forge => await GetForgeLikeAsync(
                    "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
                    "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                    minecraftVersion, ct).ConfigureAwait(false),
                LoaderKind.NeoForge => await GetForgeLikeAsync(
                    "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
                    null, minecraftVersion, ct).ConfigureAwait(false),
                _ => OperationResult<IReadOnlyList<LoaderVersionOption>>.Ok([])
            };
        }
        catch (Exception ex)
        {
            return OperationResult<IReadOnlyList<LoaderVersionOption>>.FromException(ex, ErrorCode.Network);
        }
    }

    private async Task<OperationResult<IReadOnlyList<LoaderVersionOption>>> GetFabricLikeAsync(
        Uri templateUrl, string minecraftVersion, CancellationToken ct)
    {
        var url = new Uri(string.Format(templateUrl.ToString(), Uri.EscapeDataString(minecraftVersion)));
        var entries = await http.GetJsonAsync<List<FabricLoaderEntry>>(url, ct: ct).ConfigureAwait(false);
        if (entries is null)
        {
            return OperationResult<IReadOnlyList<LoaderVersionOption>>.Fail(ErrorCode.Network, "Не удалось получить список версий лоадера.");
        }

        var options = entries
            .Select(e => e.Loader)
            .Where(l => l is not null && l.Stable)
            .Select(l => new LoaderVersionOption(l!.Version, minecraftVersion, l.Stable.ToString()))
            .ToList();

        if (options.Count == 0)
        {
            return OperationResult<IReadOnlyList<LoaderVersionOption>>.Fail(
                ErrorCode.NotFound, $"Для Minecraft {minecraftVersion} нет сборок этого лоадера.");
        }

        return OperationResult<IReadOnlyList<LoaderVersionOption>>.Ok(options);
    }

    private async Task<OperationResult<IReadOnlyList<LoaderVersionOption>>> GetForgeLikeAsync(
        string primaryUrl, string? fallbackUrl, string minecraftVersion, CancellationToken ct)
    {
        if (fallbackUrl is null)
        {
            // NeoForge: maven-metadata.xml, версии вида 21.1.77 (соответствуют MC 1.21.1).
            var xml = await http.GetStringAsync(new Uri(primaryUrl), ct: ct).ConfigureAwait(false);
            if (!xml.IsSuccess)
            {
                return OperationResult<IReadOnlyList<LoaderVersionOption>>.Fail(ErrorCode.Network, "NeoForge Maven недоступен.");
            }

            var versions = ParseMavenVersions(xml.Content)
                .Where(v => NeoForgeMatches(v, minecraftVersion))
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(v => new LoaderVersionOption(v, minecraftVersion))
                .ToList();

            return versions.Count == 0
                ? OperationResult<IReadOnlyList<LoaderVersionOption>>.Fail(ErrorCode.NotFound, $"Для Minecraft {minecraftVersion} нет сборок NeoForge.")
                : OperationResult<IReadOnlyList<LoaderVersionOption>>.Ok(versions);
        }

        var promotions = await http.GetJsonAsync<ForgePromotionsDto>(new Uri(primaryUrl), ct: ct).ConfigureAwait(false);
        var result = new List<LoaderVersionOption>();
        if (promotions?.Promos is not null)
        {
            if (promotions.Promos.TryGetValue($"{minecraftVersion}-recommended", out var recommended))
            {
                result.Add(new LoaderVersionOption(recommended, minecraftVersion, "recommended"));
            }

            if (promotions.Promos.TryGetValue($"{minecraftVersion}-latest", out var latest))
            {
                result.Add(new LoaderVersionOption(latest, minecraftVersion, "latest"));
            }
        }

        return result.Count == 0
            ? OperationResult<IReadOnlyList<LoaderVersionOption>>.Fail(ErrorCode.NotFound, $"Для Minecraft {minecraftVersion} нет сборок Forge.")
            : OperationResult<IReadOnlyList<LoaderVersionOption>>.Ok(result.DistinctBy(r => r.Version).ToList());
    }

    private static bool NeoForgeMatches(string neoVersion, string minecraftVersion)
    {
        // 1.21.1 → neo 21.1.x; 1.20.4 → 20.4.x
        var mc = minecraftVersion.Replace("1.", string.Empty);
        return neoVersion.StartsWith(mc + ".", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ParseMavenVersions(string xml)
    {
        var start = xml.IndexOf("<versions>", StringComparison.Ordinal);
        var end = xml.IndexOf("</versions>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            yield break;
        }

        var block = xml[(start + 10)..end];
        foreach (var part in block.Split("<version>", StringSplitOptions.RemoveEmptyEntries))
        {
            var close = part.IndexOf("</version>", StringComparison.Ordinal);
            if (close > 0)
            {
                yield return part[..close].Trim();
            }
        }
    }

    public async Task<OperationResult<string>> InstallLoaderAsync(
        string minecraftDirectory,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new InstallProgress("Базовые файлы", 0.05, minecraftVersion));

            var baseInstall = await gameInstaller.InstallAsync(minecraftDirectory, minecraftVersion, new InstallOptions(), progress, ct).ConfigureAwait(false);
            if (!baseInstall.IsSuccess)
            {
                return OperationResult<string>.Fail(baseInstall.Code, baseInstall.Message ?? "Базовая версия не установлена.");
            }

            return loader switch
            {
                LoaderKind.Fabric => await InstallFabricLikeAsync(minecraftDirectory, minecraftVersion, loader, loaderVersion, "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json", ct).ConfigureAwait(false),
                LoaderKind.Quilt => await InstallFabricLikeAsync(minecraftDirectory, minecraftVersion, loader, loaderVersion, "https://meta.quiltmc.org/v3/versions/loader/{0}/{1}/profile/json", ct).ConfigureAwait(false),
                LoaderKind.Forge => await InstallForgeAsync(minecraftDirectory, minecraftVersion, loader, loaderVersion, progress, ct).ConfigureAwait(false),
                LoaderKind.NeoForge => await InstallForgeAsync(minecraftDirectory, minecraftVersion, loader, loaderVersion, progress, ct).ConfigureAwait(false),
                _ => OperationResult<string>.Ok(minecraftVersion)
            };
        }
        catch (OperationCanceledException)
        {
            return OperationResult<string>.Fail(ErrorCode.Cancelled, "Установка отменена.");
        }
        catch (Exception ex)
        {
            log.Error("Loader", $"Ошибка установки {loader}", ex);
            return OperationResult<string>.FromException(ex, ErrorCode.Io);
        }
    }

    private async Task<OperationResult<string>> InstallFabricLikeAsync(
        string minecraftDirectory,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        string profileTemplate,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var options = await GetLoaderVersionsAsync(loader, minecraftVersion, ct).ConfigureAwait(false);
            if (!options.IsSuccess || options.Value!.Count == 0)
            {
                return OperationResult<string>.Fail(ErrorCode.NotFound, "Не удалось выбрать версию лоадера.");
            }

            loaderVersion = options.Value[0].Version;
        }

        var url = new Uri(string.Format(profileTemplate, Uri.EscapeDataString(minecraftVersion), Uri.EscapeDataString(loaderVersion)));
        var profile = await http.GetJsonAsync<LoaderProfileDto>(url, ct: ct).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult<string>.Fail(ErrorCode.Network, "Метаданные лоадера недоступны.");
        }

        var versionId = profile.Id ?? $"{loader.ToString().ToLowerInvariant()}-{loaderVersion}-{minecraftVersion}";
        var versionDir = Path.Combine(minecraftDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        // Приводим к формату version.json (добавляем id/inheritsFrom, если их нет).
        var json = JsonSerializer.Serialize(profile, JsonDefaults.External);
        await File.WriteAllTextAsync(Path.Combine(versionDir, $"{versionId}.json"), json, ct).ConfigureAwait(false);

        // Скачиваем библиотеки лоадера (у Fabric/Quilt они без downloads).
        var requests = new List<DownloadRequest>();
        foreach (var lib in profile.Libraries)
        {
            if (lib.BaseUrl is null)
            {
                continue;
            }

            var coordinate = MavenCoordinate.Parse(lib.Name);
            var path = coordinate.ToPath();
            requests.Add(new DownloadRequest
            {
                Id = $"loaderlib-{coordinate.ToNotation()}",
                Url = new Uri(lib.BaseUrl.TrimEnd('/') + "/" + path),
                DestinationPath = Path.Combine(minecraftDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                DisplayName = coordinate.ToNotation(),
                Group = $"loader-{versionId}",
                SkipIfVerified = false
            });
        }

        if (requests.Count > 0)
        {
            await downloads.EnqueueManyAsync(requests, null, ct).ConfigureAwait(false);
        }

        log.Info("Loader", $"Установлен {loader} {loaderVersion} для {minecraftVersion} ({versionId})");
        return OperationResult<string>.Ok(versionId);
    }

    private async Task<OperationResult<string>> InstallForgeAsync(
        string minecraftDirectory,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var options = await GetLoaderVersionsAsync(loader, minecraftVersion, ct).ConfigureAwait(false);
            if (!options.IsSuccess || options.Value!.Count == 0)
            {
                return OperationResult<string>.Fail(ErrorCode.NotFound, "Не удалось выбрать версию Forge/NeoForge.");
            }

            loaderVersion = options.Value[0].Version;
        }

        var installerFileName = loader == LoaderKind.Forge
            ? $"forge-{minecraftVersion}-{loaderVersion}-installer.jar"
            : $"neoforge-{loaderVersion}-installer.jar";

        var installerUrl = loader == LoaderKind.Forge
            ? new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{minecraftVersion}-{loaderVersion}/forge-{minecraftVersion}-{loaderVersion}-installer.jar")
            : new Uri($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar");

        var cacheDir = Path.Combine(minecraftDirectory, ".ehot-cache");
        Directory.CreateDirectory(cacheDir);
        var installerPath = Path.Combine(cacheDir, installerFileName);

        progress?.Report(new InstallProgress("Загрузка установщика", 0.4, installerFileName));
        var downloaded = await downloads.EnqueueAsync(new DownloadRequest
        {
            Id = $"installer-{loader}-{minecraftVersion}-{loaderVersion}",
            Url = installerUrl,
            DestinationPath = installerPath,
            DisplayName = installerFileName,
            Group = $"loader-{loader}",
            SkipIfVerified = true
        }, ct).ConfigureAwait(false);

        if (!downloaded)
        {
            return OperationResult<string>.Fail(ErrorCode.Network, "Не удалось скачать официальный установщик. Для Forge/NeoForge это единственный поддерживаемый способ установки.");
        }

        var java = await javaManager.EnsureJavaAsync(17, allowDownload: true, ct: ct).ConfigureAwait(false);
        if (!java.IsSuccess)
        {
            return OperationResult<string>.Fail(java.Code, java.Message ?? "Для установки Forge требуется Java 17+.");
        }

        progress?.Report(new InstallProgress("Запуск установщика", 0.6));

        var psi = new ProcessRequest(
            java.Value!.JavaPath,
            ["-jar", installerPath, "--installClient", minecraftDirectory],
            minecraftDirectory);

        var result = await runner.RunAsync(psi, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            log.Warn("Loader", $"Установщик {loader} вернул код {result.ExitCode}: {result.StandardError}");
            return OperationResult<string>.Fail(ErrorCode.InstallIncomplete,
                $"Установщик {loader} завершился с кодом {result.ExitCode}. Подробности — в логе лаунчера.");
        }

        var versionId = loader == LoaderKind.Forge
            ? $"{minecraftVersion}-forge-{loaderVersion}"
            : $"neoforge-{loaderVersion}";

        var expected = Path.Combine(minecraftDirectory, "versions", versionId);
        if (!Directory.Exists(expected))
        {
            return OperationResult<string>.Fail(ErrorCode.InstallIncomplete,
                $"Установщик завершился, но папка версии '{versionId}' не найдена. Проверьте каталог игры.");
        }

        progress?.Report(new InstallProgress("Готово", 1));
        log.Info("Loader", $"Установлен {loader} {loaderVersion} → {versionId}");
        return OperationResult<string>.Ok(versionId);
    }

    private sealed class FabricLoaderEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("loader")]
        public FabricLoaderInfo? Loader { get; set; }
    }

    private sealed class FabricLoaderInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("stable")]
        public bool Stable { get; set; }
    }

    private sealed class ForgePromotionsDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("promos")]
        public Dictionary<string, string>? Promos { get; set; }
    }
}
