using System.IO.Compression;
using System.Text.Json;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;

namespace EHOT.Launcher.Game.Install;

/// <summary>Версия OptiFine, доступная для установки.</summary>
public sealed record OptiFineVersion(string MinecraftVersion, string Type, string Patch, string FileName)
{
    /// <summary>Идентификатор установленной версии, например <c>1.20.1-OptiFine_HD_U_I6</c>.</summary>
    public string VersionId => $"{MinecraftVersion}-OptiFine_{Type}_{Patch}";

    public string Label => $"OptiFine {Type.Replace('_', ' ')} {Patch}";
}

/// <summary>
/// Установка OptiFine без запуска его GUI-инсталлятора.
/// <para>
/// OptiFine распространяется как патч-архив: внутри лежат xdelta-дельты к <c>client.jar</c>
/// ванильной версии и собственный <c>optifine.Patcher</c>. Поэтому порядок такой:
/// скачиваем архив, вызываем <c>java -cp of.jar optifine.Patcher client.jar of.jar out.jar</c>,
/// получаем готовый клиент с OptiFine, кладём его в отдельную версию и пишем version.json
/// с launchwrapper — ровно то, что делает официальный установщик, но без окон и без Windows-путей.
/// </para>
/// <para>
/// Загрузка идёт с зеркала BMCLAPI (прямые файлы); официальный сайт используется как запасной
/// источник — там ссылка выдаётся через страницу adloadx, её приходится разбирать.
/// </para>
/// </summary>
public sealed class OptiFineInstaller(
    IHttpService http,
    IDownloadManager downloads,
    IProcessRunner runner,
    Runtimes.JavaRuntimeManager java,
    ILogService log)
{
    private const string MirrorList = "https://bmclapi2.bangbang93.com/optifine/versionlist";
    private const string MirrorJar = "https://bmclapi2.bangbang93.com/optifine/{0}/{1}/{2}";
    private const string OfficialAdloadx = "https://optifine.net/adloadx?f={0}";
    private const string OfficialDownloadx = "https://optifine.net/downloadx?f={0}&x={1}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // CompositeFormat кэшируется: форматирование URL не должно парсить строку каждый раз.
    private static readonly System.Text.CompositeFormat MirrorJarFormat = System.Text.CompositeFormat.Parse(MirrorJar);
    private static readonly System.Text.CompositeFormat OfficialAdloadxFormat = System.Text.CompositeFormat.Parse(OfficialAdloadx);
    private static readonly System.Text.CompositeFormat OfficialDownloadxFormat = System.Text.CompositeFormat.Parse(OfficialDownloadx);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Список версий OptiFine (свежие сверху). Кэш — на время жизни объекта.</summary>
    public async Task<OperationResult<IReadOnlyList<OptiFineVersion>>> GetVersionsAsync(CancellationToken ct = default)
    {
        try
        {
            var items = await http.GetJsonAsync<List<OptiFineDto>>(new Uri(MirrorList), ct: ct).ConfigureAwait(false);
            if (items is null || items.Count == 0)
            {
                return OperationResult<IReadOnlyList<OptiFineVersion>>.Fail(ErrorCode.Network,
                    "Зеркало OptiFine вернуло пустой список версий.");
            }

            var list = items
                .Where(i => !string.IsNullOrWhiteSpace(i.McVersion) && !string.IsNullOrWhiteSpace(i.Type)
                            && !string.IsNullOrWhiteSpace(i.Patch))
                .Select(i => new OptiFineVersion(i.McVersion!, i.Type!, i.Patch!, i.FileName ?? string.Empty))
                .GroupBy(v => v.VersionId)
                .Select(g => g.First())
                // Свежие версии игры сверху; внутри версии стабильные сборки выше preview.
                .OrderByDescending(v => ParseMinecraftVersion(v.MinecraftVersion))
                .ThenBy(v => IsPreview(v.Patch) ? 1 : 0)
                .ThenByDescending(v => v.Patch, StringComparer.Ordinal)
                .ToList();

            return list.Count == 0
                ? OperationResult<IReadOnlyList<OptiFineVersion>>.Fail(ErrorCode.Network,
                    "В списке OptiFine нет ни одной полной записи.")
                : OperationResult<IReadOnlyList<OptiFineVersion>>.Ok(list);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.Warn("OptiFine", $"Список версий недоступен: {ex.Message}");
            return OperationResult<IReadOnlyList<OptiFineVersion>>.Fail(ErrorCode.Network,
                "Не удалось получить список версий OptiFine: " + ex.Message);
        }
    }

    /// <summary>
    /// Ставит OptiFine для указанной версии Minecraft и возвращает id версии для запуска.
    /// Ванильная версия должна быть уже установлена — патч применяется к её client.jar.
    /// </summary>
    public async Task<OperationResult<string>> InstallAsync(
        string minecraftDirectory,
        OptiFineVersion version,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            return OperationResult<string>.Fail(ErrorCode.Validation, "Не указан каталог игры.");
        }

        var baseDir = Path.Combine(minecraftDirectory, "versions", version.MinecraftVersion);
        var baseJson = Path.Combine(baseDir, $"{version.MinecraftVersion}.json");
        var baseJar = Path.Combine(baseDir, $"{version.MinecraftVersion}.jar");
        if (!File.Exists(baseJson) || !File.Exists(baseJar))
        {
            return OperationResult<string>.Fail(ErrorCode.InstallIncomplete,
                $"Сначала установите Minecraft {version.MinecraftVersion}: OptiFine ставится патчем поверх её client.jar.");
        }

        var targetId = version.VersionId;
        var targetDir = Path.Combine(minecraftDirectory, "versions", targetId);
        var targetJar = Path.Combine(targetDir, $"{targetId}.jar");
        var targetJson = Path.Combine(targetDir, $"{targetId}.json");

        var archivesDir = Path.Combine(minecraftDirectory, "libraries", "optifine", "archives");
        Directory.CreateDirectory(archivesDir);
        Directory.CreateDirectory(targetDir);

        var archives = new List<string>();

        try
        {
            progress?.Report(new InstallProgress("OptiFine: Java", 0.05));
            var runtime = await java.EnsureJavaAsync(8, true, ct: ct).ConfigureAwait(false);
            if (!runtime.IsSuccess)
            {
                return OperationResult<string>.Fail(runtime.Code, runtime.Message ?? string.Empty);
            }

            var javaPath = runtime.Value!.JavaPath;

            progress?.Report(new InstallProgress("OptiFine: загрузка", 0.15, version.FileName));
            var patchJar = await DownloadPatchAsync(version, archivesDir, ct).ConfigureAwait(false);
            if (!patchJar.IsSuccess)
            {
                return OperationResult<string>.Fail(patchJar.Code, patchJar.Message ?? string.Empty);
            }

            var patchPath = patchJar.Value!;

            // launchwrapper и сам OptiFine попадают в classpath как отдельные библиотеки —
            // так же, как их раскладывает официальный установщик.
            progress?.Report(new InstallProgress("OptiFine: библиотеки", 0.35));
            // Путь строго по Maven-координате optifine:OptiFine:<mc>_<type>_<patch>,
            // иначе лаунчер не найдёт библиотеку при сборке classpath.
            var optifineLib = Path.Combine(minecraftDirectory, "libraries", "optifine", "OptiFine",
                $"{version.MinecraftVersion}_{version.Type}_{version.Patch}",
                $"OptiFine-{version.MinecraftVersion}_{version.Type}_{version.Patch}.jar");
            if (!File.Exists(optifineLib))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(optifineLib)!);
                File.Copy(patchPath, optifineLib, overwrite: true);
            }

            var launchwrapper = await EnsureLaunchwrapperAsync(patchPath, minecraftDirectory, archivesDir, ct)
                .ConfigureAwait(false);
            if (!launchwrapper.IsSuccess)
            {
                return OperationResult<string>.Fail(launchwrapper.Code, launchwrapper.Message ?? string.Empty);
            }

            archives.Add(patchPath);

            progress?.Report(new InstallProgress("OptiFine: патч client.jar", 0.5));
            var patched = Path.Combine(targetDir, "patched.tmp.jar");
            var patchResult = await runner.RunAsync(new ProcessRequest(
                javaPath,
                [
                    "-Xmx1G",
                    "-cp", patchPath,
                    "optifine.Patcher",
                    baseJar,
                    patchPath,
                    patched
                ],
                minecraftDirectory), ct).ConfigureAwait(false);

            if (!patchResult.Succeeded || !File.Exists(patched))
            {
                return OperationResult<string>.Fail(ErrorCode.InstallIncomplete,
                    "Патч OptiFine не применился: " + Tail(patchResult.StandardError, patchResult.StandardOutput));
            }

            File.Move(patched, targetJar, overwrite: true);

            progress?.Report(new InstallProgress("OptiFine: version.json", 0.85));
            var libraries = new List<object>
            {
                new { name = $"optifine:OptiFine:{version.MinecraftVersion}_{version.Type}_{version.Patch}" },
                new { name = $"optifine:launchwrapper-of:{launchwrapper.Value}" }
            };

            var json = new
            {
                id = targetId,
                inheritsFrom = version.MinecraftVersion,
                time = DateTimeOffset.UtcNow.ToString("O"),
                releaseTime = DateTimeOffset.UtcNow.ToString("O"),
                type = "release",
                mainClass = "net.minecraft.launchwrapper.Launch",
                arguments = new
                {
                    game = new object[] { "--tweakClass", "optifine.OptiFineTweaker" },
                    jvm = Array.Empty<string>()
                },
                libraries
            };

            await File.WriteAllTextAsync(targetJson, JsonSerializer.Serialize(json, WriteOptions), ct)
                .ConfigureAwait(false);

            progress?.Report(new InstallProgress("OptiFine: готово", 1));
            log.Info("OptiFine", $"Установлен {targetId}");
            return OperationResult<string>.Ok(targetId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Warn("OptiFine", $"Установка не удалась: {ex.Message}");
            return OperationResult<string>.Fail(ErrorCode.Io, "Не удалось установить OptiFine: " + ex.Message);
        }
    }

    /// <summary>Скачивает патч-архив: сначала зеркало, затем официальный сайт.</summary>
    private async Task<OperationResult<string>> DownloadPatchAsync(
        OptiFineVersion version, string archivesDir, CancellationToken ct)
    {
        var fileName = string.IsNullOrWhiteSpace(version.FileName)
            ? $"OptiFine_{version.MinecraftVersion}_{version.Type}_{version.Patch}.jar"
            : version.FileName;
        var target = Path.Combine(archivesDir, fileName);
        if (IsPatchArchive(target))
        {
            return OperationResult<string>.Ok(target);
        }

        var mirrorUrl = new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MirrorJarFormat, version.MinecraftVersion, version.Type, version.Patch));

        var downloaded = await downloads.EnqueueAsync(new DownloadRequest
        {
            Id = "optifine-" + fileName,
            Url = mirrorUrl,
            DestinationPath = target,
            DisplayName = fileName,
            Group = "optifine"
        }, ct).ConfigureAwait(false);

        if (downloaded && IsPatchArchive(target))
        {
            return OperationResult<string>.Ok(target);
        }

        // Официальный сайт: ссылка выдаётся на странице adloadx как downloadx?f=...&x=...
        log.Warn("OptiFine", "Зеркало не отдало файл, пробуем optifine.net");
        try
        {
            var page = await http.GetStringAsync(new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                OfficialAdloadxFormat, fileName)), ct: ct).ConfigureAwait(false);
            var match = System.Text.RegularExpressions.Regex.Match(page.Content,
                @"downloadx\?f=[^""'&]+&x=[0-9a-f]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return OperationResult<string>.Fail(ErrorCode.Network,
                    "optifine.net не выдал прямую ссылку — файл придётся скачать вручную.");
            }

            var direct = await downloads.EnqueueAsync(new DownloadRequest
            {
                Id = "optifine-official-" + fileName,
                Url = new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    OfficialDownloadxFormat, Uri.EscapeDataString(fileName),
                    match.Value.Split("x=", StringSplitOptions.RemoveEmptyEntries)[^1])),
                DestinationPath = target,
                DisplayName = fileName,
                Group = "optifine"
            }, ct).ConfigureAwait(false);

            return direct && IsPatchArchive(target)
                ? OperationResult<string>.Ok(target)
                : OperationResult<string>.Fail(ErrorCode.Network, "OptiFine не скачался: файл повреждён.");
        }
        catch (HttpRequestException ex)
        {
            return OperationResult<string>.Fail(ErrorCode.Network, "OptiFine не скачался: " + ex.Message);
        }
    }

    /// <summary>Патч-архив OptiFine — это zip с patch.cfg; по нему отличаем файл от HTML-ошибки.</summary>
    private static bool IsPatchArchive(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 1_000_000)
            {
                return false;
            }

            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("patch.cfg") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>launchwrapper-of лежит внутри патч-архива; версия указана в launchwrapper-of.txt.</summary>
    private static async Task<OperationResult<string>> EnsureLaunchwrapperAsync(
        string patchPath, string minecraftDirectory, string archivesDir, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            using var zip = ZipFile.OpenRead(patchPath);
            var txt = zip.GetEntry("launchwrapper-of.txt");
            var jar = zip.GetEntry("launchwrapper-of-2.3.jar")
                      ?? zip.Entries.FirstOrDefault(e => e.Name.StartsWith("launchwrapper-of", StringComparison.Ordinal)
                                                         && e.Name.EndsWith(".jar", StringComparison.Ordinal));
            if (jar is null)
            {
                return OperationResult<string>.Fail(ErrorCode.Validation,
                    "В архиве OptiFine нет launchwrapper — файл повреждён или это другая сборка.");
            }

            var version = "2.3";
            if (txt is not null)
            {
                await using var stream = txt.Open();
                using var reader = new StreamReader(stream);
                var read = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                if (read.Length > 0)
                {
                    version = read;
                }
            }

            var destination = Path.Combine(minecraftDirectory, "libraries", "optifine", "launchwrapper-of",
                version, $"launchwrapper-of-{version}.jar");
            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = jar.Open();
                await using var target = File.Create(destination);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            return OperationResult<string>.Ok(version);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return OperationResult<string>.Fail(ErrorCode.Io, "launchwrapper не распаковался: " + ex.Message);
        }
    }

    private static string Tail(string stderr, string stdout)
    {
        var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "инициализатор OptiFine завершился с ошибкой без вывода.";
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', lines.TakeLast(4)).Trim();
    }

    private static bool IsPreview(string patch)
        => patch.Contains("pre", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сортировочный ключ версии Minecraft: 1.20.1 → 1.020001.</summary>
    private static double ParseMinecraftVersion(string id)
    {
        var parts = id.Split('.', 3);
        var major = int.TryParse(parts[0], out var a) ? a : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        var patch = parts.Length > 2 && int.TryParse(new string(parts[2].TakeWhile(char.IsDigit).ToArray()), out var c) ? c : 0;
        return (major * 10000) + (minor * 100) + patch;
    }

    private sealed class OptiFineDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("mcversion")]
        public string? McVersion { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("patch")]
        public string? Patch { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string? FileName { get; set; }
    }
}
