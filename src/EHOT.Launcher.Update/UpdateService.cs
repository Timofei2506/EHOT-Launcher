using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Update;

/// <summary>Настройки канала обновлений.</summary>
public sealed record UpdateOptions
{
    /// <summary>Репозиторий GitHub вида owner/name. Пусто — канал не настроен.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Куда кладём скачанные файлы (обычно AppData/updates).</summary>
    public string StagingRoot { get; init; } = string.Empty;

    /// <summary>Полный путь к текущему исполняемому файлу приложения.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Проверять предрелизы (beta).</summary>
    public bool IncludePrerelease { get; init; } = true;

    public string? GitHubToken { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Repository)
                                && Repository.Contains('/', StringComparison.Ordinal)
                                && !string.IsNullOrWhiteSpace(StagingRoot);
}

/// <summary>
/// Автообновление через публичные релизы GitHub.
///
/// Схема: GitHub Releases API → выбрать .zip/.dmg/.exe под нашу платформу → скачать во временную папку →
/// подменить файлы после выхода приложения и перезапустить его.
///
/// Честность: если канал не настроен или сеть недоступна — соответствующий статус, а не «всё хорошо».
/// Хэши проверяются, только если релиз их публикует (`*.sha256` в ассетах).
/// </summary>
public sealed class GitHubUpdateService(
    UpdateOptions options,
    ILogService log,
    IPlatformInfo platform) : IUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // Запросы к GitHub идут мимо общего IHttpService: нужны произвольные заголовки,
    // потоковая загрузка крупных архивов и редирект на objects.githubusercontent.com.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    })
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    public Version CurrentVersion
    {
        get
        {
            var raw = Environment.ProcessPath is { } path
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion
                : null;
            return ParseVersion(raw) ?? new Version(0, 1, 0);
        }
    }

    public bool IsConfigured => options.IsConfigured;

    public async Task<UpdateCheckResult> CheckAsync(bool includePrerelease = true, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return new UpdateCheckResult(
                UpdateAvailability.NotConfigured,
                null,
                CurrentVersion,
                "Канал обновлений не настроен: в сборке не указан репозиторий релизов.");
        }

        try
        {
            var url = $"https://api.github.com/repos/{options.Repository}/releases?per_page=10";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("EHOT-Launcher/" + CurrentVersion);
            if (!string.IsNullOrWhiteSpace(options.GitHubToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GitHubToken);
            }

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Warn("Update", $"GitHub вернул {(int)response.StatusCode} при проверке релизов.");
                return new UpdateCheckResult(
                    UpdateAvailability.Failed, null, CurrentVersion,
                    $"Сервер обновлений вернул {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonOptions) ?? [];
            var current = CurrentVersion;
            var checkPrerelease = includePrerelease && options.IncludePrerelease;

            var best = releases
                .Where(r => !r.Draft)
                .Where(r => checkPrerelease || !r.Prerelease)
                .Select(ToReleaseInfo)
                .Where(r => r is not null)
                .Select(r => r!)
                .Where(r => r.Version > current)
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();

            if (best is null)
            {
                return new UpdateCheckResult(UpdateAvailability.UpToDate, null, current, "Установлена последняя версия.");
            }

            log.Info("Update", $"Доступно обновление {best.Version} (сейчас {current}).");
            return new UpdateCheckResult(UpdateAvailability.Available, best, current, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.Warn("Update", $"Проверка обновлений не удалась: {ex.Message}");
            return new UpdateCheckResult(
                UpdateAvailability.Failed, null, CurrentVersion,
                "Не удалось связаться с сервером обновлений: " + ex.Message);
        }
    }

    public async Task<UpdatePlan> DownloadAsync(
        ReleaseInfo release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var asset = SelectAsset(release.Assets)
                    ?? throw new InvalidOperationException(
                        $"В релизе {release.TagName} нет файла для этой платформы ({platform.OsName}/{platform.Architecture}).");

        var staging = Path.Combine(options.StagingRoot, release.TagName);
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, asset.Name);

        progress?.Report(new UpdateProgress(UpdateStage.Downloading, 0, $"Скачивание {asset.Name}"));

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("EHOT-Launcher/" + CurrentVersion);
        if (!string.IsNullOrWhiteSpace(options.GitHubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GitHubToken);
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    progress?.Report(new UpdateProgress(
                        UpdateStage.Downloading,
                        Math.Clamp((double)received / total, 0, 1),
                        $"{received / 1024 / 1024} из {total / 1024 / 1024} МБ"));
                }
            }
        }

        progress?.Report(new UpdateProgress(UpdateStage.Verifying, 1, "Проверка файла"));

        var actualSize = new FileInfo(target).Length;
        if (asset.SizeBytes > 0 && actualSize != asset.SizeBytes)
        {
            File.Delete(target);
            throw new InvalidOperationException(
                $"Размер скачанного файла не совпал: {actualSize} вместо {asset.SizeBytes}. Файл удалён.");
        }

        if (release.Sha256 is { Length: 64 } expected)
        {
            await using var stream = File.OpenRead(target);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidOperationException("Контрольная сумма обновления не совпала. Файл удалён.");
            }
        }

        log.Info("Update", $"Обновление скачано: {target} ({actualSize} байт)");
        progress?.Report(new UpdateProgress(UpdateStage.Ready, 1, "Готово к установке"));
        return new UpdatePlan(release, asset, target, staging);
    }

    public async Task ApplyAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            // На macOS и Linux обновление ставится заменой .app/каталога — это делает установщик
            // или пользователь. Мы не подменяем файлы у работающего приложения вслепую.
            throw new PlatformNotSupportedException(
                "Автоматическая подмена файлов поддерживается только для Windows-сборки. " +
                "На macOS/Linux скачанный файл нужно установить вручную (он уже лежит в папке обновлений).");
        }

        if (!plan.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // .exe-установщик: запускаем его отдельно и выходим — он сам обновит файлы.
            if (!plan.Asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Формат обновления не поддерживается: {plan.Asset.Name}");
            }

            log.Info("Update", "Запуск установщика обновления");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(plan.TargetPath)
            {
                UseShellExecute = true
            });
            return;
        }

        var executable = options.ExecutablePath
                         ?? Environment.ProcessPath
                         ?? throw new InvalidOperationException("Не удалось определить путь текущего приложения.");

        var appDirectory = Path.GetDirectoryName(executable)
                           ?? throw new InvalidOperationException("Не удалось определить каталог приложения.");

        // Скрипт ждёт выхода лаунчера, распаковывает архив поверх каталога и запускает приложение снова.
        var script = Path.Combine(plan.StagingDirectory, "apply-update.cmd");
        var logFile = Path.Combine(plan.StagingDirectory, "apply-update.log");
        var scriptBody = $"""
            @echo off
            chcp 65001 >nul
            setlocal
            echo [%DATE% %TIME%] ожидание выхода лаунчера >> "{logFile}"
            :wait
            tasklist /FI "IMAGENAME eq {Path.GetFileName(executable)}" 2>nul | find /I "{Path.GetFileName(executable)}" >nul
            if not errorlevel 1 (
              timeout /t 2 /nobreak >nul
              goto wait
            )
            echo [%DATE% %TIME%] распаковка {plan.TargetPath} >> "{logFile}"
            powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '{plan.TargetPath}' -DestinationPath '{appDirectory}' -Force" >> "{logFile}" 2>&1
            if errorlevel 1 (
              echo [%DATE% %TIME%] ОШИБКА распаковки >> "{logFile}"
              pause
              exit /b 1
            )
            echo [%DATE% %TIME%] запуск лаунчера >> "{logFile}"
            start "" "{executable}"
            exit /b 0
            """;

        await File.WriteAllTextAsync(script, scriptBody, ct).ConfigureAwait(false);

        log.Info("Update", $"Установка обновления через {script}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        });
    }

    /// <summary>
    /// Выбирает файл под текущую ОС и архитектуру из списка ассетов релиза.
    /// Приоритет: установщик → архив, совпадение по RID (win-x64, osx-arm64, …).
    /// </summary>
    public static ReleaseAsset? SelectAsset(IReadOnlyList<ReleaseAsset> assets)
    {
        var osToken = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = ArchToken();

        var candidates = assets
            .Select(a => (Asset: a, Name: a.Name.ToLowerInvariant()))
            // Имя файла должно содержать ОС и архитектуру: ehоt-0.2.0-osx-arm64.dmg
            .Where(x => x.Name.Contains(osToken, StringComparison.Ordinal)
                        && (x.Name.Contains(arch, StringComparison.Ordinal)
                            || OperatingSystem.IsMacOS() && x.Name.Contains("universal", StringComparison.Ordinal)))
            .ToList();

        // Приоритет: установщик (проще пользователю) → архив (портативно) → образ.
        string[] preferred = OperatingSystem.IsWindows()
            ? [".exe", ".zip"]
            : OperatingSystem.IsMacOS() ? [".dmg", ".zip"] : [".tar.gz", ".zip"];

        foreach (var extension in preferred)
        {
            var match = candidates.FirstOrDefault(x => x.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            if (match.Asset is not null)
            {
                return match.Asset;
            }
        }

        return null;
    }

    private static string ArchToken() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => "x64"
    };

    private static ReleaseInfo? ToReleaseInfo(GitHubRelease release)
    {
        var version = ParseVersion(release.TagName) ?? ParseVersion(release.Name);
        if (version is null)
        {
            return null;
        }

        var assets = release.Assets
            .Select(a => new ReleaseAsset(a.Name, new Uri(a.BrowserDownloadUrl), a.Size, a.Id))
            .ToList();
        // Хэш релиза (если опубликован как ассет *.sha256) подставляется в ReleaseInfo.Sha256
        // вызывающей стороной после скачивания самого файла хэша.

        return new ReleaseInfo(
            version,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            release.Body,
            new Uri(release.HtmlUrl),
            release.PublishedAt,
            release.Prerelease,
            assets,
            null);
    }

    /// <summary>Достаёт версию из тега: v0.1.0-beta.1 → 0.1.0.</summary>
    public static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+']);
        if (cut > 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; init; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed record GitHubAsset
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
