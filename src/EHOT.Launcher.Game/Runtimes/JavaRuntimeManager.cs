using System.Globalization;
using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Archives;
using EHOT.Launcher.Game.Install;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Platform.Shared;

namespace EHOT.Launcher.Game.Runtimes;

public sealed record JavaInstallResult(string JavaPath, int MajorVersion, string Vendor);

/// <summary>
/// Управление Java-рантаймами: обнаружение на системе и докачка официальных сборок Eclipse Temurin
/// (Adoptium API) в каталог лаунчера, если нужной версии нет.
/// </summary>
public sealed class JavaRuntimeManager(
    IJavaRuntimeService javaService,
    IHttpService http,
    IDownloadManager downloads,
    IArchiveExtractor archives,
    IAppPaths paths,
    IPlatformInfo platform,
    ILogService log)
{
    private const string AdoptiumLatest = "https://api.adoptium.net/v3/assets/latest/{0}/hotspot?architecture={1}&image_type=jre&vendor=eclipse&os={2}";

    // CompositeFormat кэшируется один раз: форматирование не должно парсить строку на каждый запрос.
    private static readonly System.Text.CompositeFormat AdoptiumFormat = System.Text.CompositeFormat.Parse(AdoptiumLatest);

    public Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(CancellationToken ct = default)
        => javaService.DiscoverAsync(ct);

    public JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, int requiredMajor)
        => javaService.SelectBest(runtimes, requiredMajor);

    /// <summary>
    /// Возвращает путь к java для нужной мажорной версии, при необходимости скачивает Adoptium JRE.
    /// </summary>
    public async Task<OperationResult<JavaInstallResult>> EnsureJavaAsync(
        int requiredMajor,
        bool allowDownload = true,
        IProgress<JavaProgress>? progress = null,
        CancellationToken ct = default)
    {
        var discovered = await DiscoverAsync(ct).ConfigureAwait(false);
        var best = Select(discovered, requiredMajor);
        if (best is not null)
        {
            var exe = ResolveExecutable(best.Path);
            return OperationResult<JavaInstallResult>.Ok(new JavaInstallResult(exe, best.MajorVersion, best.Vendor));
        }

        if (!allowDownload)
        {
            return OperationResult<JavaInstallResult>.Fail(
                ErrorCode.JavaNotFound,
                $"Java {requiredMajor} не найдена на этом компьютере. Установите JRE {requiredMajor} или разрешите лаунчеру скачать её.");
        }

        return await DownloadJavaAsync(requiredMajor, progress, ct).ConfigureAwait(false);
    }

    private async Task<OperationResult<JavaInstallResult>> DownloadJavaAsync(
        int major,
        IProgress<JavaProgress>? progress,
        CancellationToken ct)
    {
        var os = platform.IsWindows ? "windows" : platform.IsMacOS ? "mac" : "linux";
        var arch = platform.Architecture switch
        {
            "arm64" => "aarch64",
            "x64" => "x64",
            var other => other
        };

        var url = new Uri(string.Format(CultureInfo.InvariantCulture, AdoptiumFormat, major, arch, os));
        progress?.Report(new JavaProgress("Поиск JRE", 0.05, $"Java {major} для {os}/{arch}"));

        var assets = await http.GetJsonAsync<List<AdoptiumAsset>>(url, ct: ct).ConfigureAwait(false);
        var package = assets?.FirstOrDefault()?.Binary?.Package;
        if (package?.Link is null)
        {
            return OperationResult<JavaInstallResult>.Fail(
                ErrorCode.NotFound,
                $"Не удалось найти JRE {major} для {os}/{arch} в Adoptium. Проверьте соединение.");
        }

        var runtimesDir = Path.Combine(paths.AppDataDirectory, "runtimes");
        Directory.CreateDirectory(runtimesDir);
        var archivePath = Path.Combine(paths.CacheDirectory, package.Name ?? $"jre-{major}.zip");
        Directory.CreateDirectory(paths.CacheDirectory);

        progress?.Report(new JavaProgress("Загрузка JRE", 0.15, package.Name));

        var ok = await downloads.EnqueueAsync(new DownloadRequest
        {
            Id = $"jre-{major}-{os}-{arch}",
            Url = new Uri(package.Link),
            DestinationPath = archivePath,
            DisplayName = $"Java {major} ({package.Name})",
            ExpectedSize = package.Size,
            ExpectedSha512 = package.Checksum,
            Group = "java",
            SkipIfVerified = true
        }, ct).ConfigureAwait(false);

        if (!ok)
        {
            return OperationResult<JavaInstallResult>.Fail(ErrorCode.Network, "Не удалось скачать пакет Java.");
        }

        var targetDir = Path.Combine(runtimesDir, $"temurin-{major}-{os}-{arch}");
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        progress?.Report(new JavaProgress("Распаковка JRE", 0.8));

        try
        {
            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(targetDir);
                var result = await Task.Run(() => TarArchive.ExtractAsync(archivePath, targetDir, ct), ct).ConfigureAwait(false);
                if (!result)
                {
                    return OperationResult<JavaInstallResult>.Fail(ErrorCode.Io, "Не удалось распаковать архив Java (tar.gz).");
                }
            }
            else
            {
                await archives.ExtractZipAsync(archivePath, targetDir, null, null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.Error("Java", "Ошибка распаковки JRE", ex);
            return OperationResult<JavaInstallResult>.FromException(ex, ErrorCode.Io);
        }

        // Внутри архива может быть вложенная папка (jdk-17.0.x+y) — ищем bin/java.
        var home = FindJavaHome(targetDir);
        if (home is null)
        {
            return OperationResult<JavaInstallResult>.Fail(ErrorCode.JavaNotFound, "В скачанном архиве не найден bin/java.");
        }

        var exe = ResolveExecutable(home);
        log.Info("Java", $"Установлена Java {major}: {exe}");
        progress?.Report(new JavaProgress("Готово", 1));

        return OperationResult<JavaInstallResult>.Ok(new JavaInstallResult(exe, major, "Eclipse Adoptium"));
    }

    private static string? FindJavaHome(string root)
    {
        var direct = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(direct))
        {
            return root;
        }

        var macDirect = Path.Combine(root, "Contents", "Home", "bin", "java");
        if (File.Exists(macDirect))
        {
            return Path.Combine(root, "Contents", "Home");
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var candidate = Path.Combine(dir, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
                if (File.Exists(candidate))
                {
                    return dir;
                }

                var macCandidate = Path.Combine(dir, "Contents", "Home", "bin", "java");
                if (File.Exists(macCandidate))
                {
                    return Path.Combine(dir, "Contents", "Home");
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static string ResolveExecutable(string home)
    {
        var direct = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(direct))
        {
            return direct;
        }

        var mac = Path.Combine(home, "Contents", "Home", "bin", "java");
        return File.Exists(mac) ? mac : direct;
    }

    private sealed class AdoptiumAsset
    {
        [JsonPropertyName("binary")]
        public AdoptiumBinary? Binary { get; set; }
    }

    private sealed class AdoptiumBinary
    {
        [JsonPropertyName("package")]
        public AdoptiumPackage? Package { get; set; }
    }

    private sealed class AdoptiumPackage
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("checksum")]
        public string? Checksum { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed record JavaProgress(string Stage, double Fraction, string? Detail = null);
