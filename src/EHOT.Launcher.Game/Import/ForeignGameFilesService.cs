using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;

namespace EHOT.Launcher.Game.Import;

/// <summary>Найденная версия Minecraft в чужом лаунчере (готова к переиспользованию).</summary>
public sealed class ForeignVersionInfo
{
    public required string SourceLabel { get; init; }
    public required string SourceDirectory { get; init; }
    public required string VersionId { get; init; }
    public required string VersionDirectory { get; init; }
    public long SizeBytes { get; init; }
    public long LibrariesBytes { get; init; }
    public long AssetsBytes { get; init; }
    public bool HasLibraries { get; init; }
    public bool HasAssets { get; init; }
    /// <summary>Версия уже есть в нашем каталоге — повторно не импортируем.</summary>
    public bool AlreadyImported { get; init; }

    public string SizeLabel => BytesToHuman(SizeBytes);

    public string DataLabel
    {
        get
        {
            var parts = new List<string>();
            if (HasLibraries) parts.Add($"библиотеки {BytesToHuman(LibrariesBytes)}");
            if (HasAssets) parts.Add($"ассеты {BytesToHuman(AssetsBytes)}");
            return parts.Count == 0 ? "только клиент" : string.Join(" · ", parts);
        }
    }

    public static string BytesToHuman(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} ГБ"
    };
}

/// <summary>Итог сканирования внешних установок Minecraft.</summary>
public sealed class ForeignScanResult
{
    public IReadOnlyList<ForeignVersionInfo> Versions { get; init; } = [];
    public IReadOnlyList<string> SearchedRoots { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}

/// <summary>Итог импорта игровых файлов.</summary>
public sealed class ForeignImportResult
{
    public bool Success { get; set; }
    public int VersionsImported { get; set; }
    public int FilesLinked { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public IReadOnlyList<string> ImportedVersionIds { get; set; } = [];
    public IReadOnlyList<string> SkippedVersionIds { get; set; } = [];
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// «Кража» версий Minecraft из других лаунчеров: находит готовые установки
/// (официальный лаунчер, TLauncher, Legacy Launcher, PrismLauncher, MultiMC, CurseForge),
/// показывает их список и переносит клиента, библиотеки и ассеты в наш каталог
/// жёсткими ссылками (мгновенно, без дублирования места на том же томе), с откатом на копирование.
/// <para>Чужой лаунчер при этом никогда не изменяется — мы только читаем или создаём ссылки на его файлы.</para>
/// </summary>
public interface IForeignGameFilesService
{
    /// <summary>Ищет установки в известных местах (платформенная разводка путей).</summary>
    Task<ForeignScanResult> ScanCommonAsync(string destinationMinecraftDirectory, CancellationToken ct = default);

    /// <summary>Сканирует указанную пользователем папку как установку другого лаунчера.</summary>
    Task<ForeignScanResult> ScanDirectoryAsync(string minecraftDirectory, string label, string destinationMinecraftDirectory, CancellationToken ct = default);

    Task<ForeignImportResult> ImportAsync(
        IReadOnlyList<ForeignVersionInfo> selected,
        string destinationMinecraftDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Быстрый best-effort перенос ДО установки: подтягивает ассеты и библиотеки целиком
    /// и только папку versions/&lt;versionId&gt; из чужих лаунчеров (жёсткие ссылки, без дублирования
    /// места). Вызывается из установщика: если файлы уже есть локально, они не трогаются.
    /// Никогда не бросает наружу — любая ошибка означает «просто не подтянулось».
    /// </summary>
    Task<(int Linked, int Copied)> LinkMissingAsync(string destinationMinecraftDirectory, string versionId, CancellationToken ct = default);
}

public sealed class ForeignGameFilesService(ILogService log) : IForeignGameFilesService
{
    /// <summary>Создание жёсткой ссылки: CreateHardLinkW (Windows) / link(2) (Unix).</summary>
    private static class HardLink
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpFileName,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpExistingFileName,
            IntPtr lpSecurityAttributes);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int LinkUnix(IntPtr oldpath, IntPtr newpath);

        public static bool TryCreate(string linkPath, string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return CreateHardLinkW(linkPath, target, IntPtr.Zero);
                }

                var oldPtr = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(target);
                var newPtr = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(linkPath);
                try
                {
                    return LinkUnix(oldPtr, newPtr) == 0;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(oldPtr);
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(newPtr);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<ForeignScanResult> ScanCommonAsync(string destinationMinecraftDirectory, CancellationToken ct = default)
        => Task.Run(() => ScanCore(CandidateRoots(), destinationMinecraftDirectory, ct), ct);

    public Task<ForeignScanResult> ScanDirectoryAsync(string minecraftDirectory, string label, string destinationMinecraftDirectory, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(minecraftDirectory) || !Directory.Exists(minecraftDirectory))
            {
                return new ForeignScanResult { Summary = $"Каталог не найден: {minecraftDirectory}", SearchedRoots = [minecraftDirectory] };
            }

            return ScanCore([(label, minecraftDirectory)], destinationMinecraftDirectory, ct);
        }, ct);

    private static ForeignScanResult ScanCore(IReadOnlyList<(string Label, string Root)> roots, string destinationMinecraftDirectory, CancellationToken ct)
    {
        var versions = new List<ForeignVersionInfo>();
        var searched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, root) in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            searched.Add(root);
            var rootVersions = EnumerateVersions(root);
            if (rootVersions.Count == 0)
            {
                continue;
            }

            var librariesBytes = Measure(Path.Combine(root, "libraries"), ct);
            var assetsBytes = Measure(Path.Combine(root, "assets"), ct);
            var hasLibraries = Directory.Exists(Path.Combine(root, "libraries"));
            var hasAssets = Directory.Exists(Path.Combine(root, "assets"));

            foreach (var (id, versionDir, size) in rootVersions)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(root + "|" + id))
                {
                    continue;
                }

                var destDir = Path.Combine(destinationMinecraftDirectory, "versions", id);
                versions.Add(new ForeignVersionInfo
                {
                    SourceLabel = label,
                    SourceDirectory = root,
                    VersionId = id,
                    VersionDirectory = versionDir,
                    SizeBytes = size,
                    LibrariesBytes = librariesBytes,
                    AssetsBytes = assetsBytes,
                    HasLibraries = hasLibraries,
                    HasAssets = hasAssets,
                    AlreadyImported = File.Exists(Path.Combine(destDir, id + ".json"))
                });
            }
        }

        var ordered = versions.OrderByDescending(v => v.VersionDirectory).ToList();
        var summary = ordered.Count == 0
            ? "Готовых версий не найдено. Если другой лаунчер установлен в нестандартном месте — укажите папку вручную."
            : $"Найдено версий: {ordered.Count} из {searched.Count} источник(ов). Отметьте нужные и импортируйте.";

        return new ForeignScanResult { Versions = ordered, SearchedRoots = searched, Summary = summary };
    }

    /// <summary>Известные расположения других лаунчеров (по платформе).</summary>
    private static List<(string Label, string Root)> CandidateRoots()
    {
        var roots = new List<(string, string)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA")
                          ?? Path.Combine(home, "AppData", "Roaming");
            roots.Add(("Официальный лаунчер", Path.Combine(appData, ".minecraft")));
            roots.Add(("TLauncher", Path.Combine(appData, ".tlauncher", "minecraft")));
            roots.Add(("TLauncher", Path.Combine(appData, ".tlauncher", ".minecraft")));
            roots.Add(("TLauncher", Path.Combine(appData, "tlauncher")));
            roots.Add(("Legacy Launcher", Path.Combine(appData, ".legacylauncher", ".minecraft")));
            roots.Add(("Legacy Launcher", Path.Combine(appData, "LegacyLauncher", ".minecraft")));
            roots.Add(("Legacy Launcher", Path.Combine(appData, "legacylauncher", ".minecraft")));
            roots.Add(("CurseForge", Path.Combine(home, "curseforge", "minecraft")));
            roots.Add(("CurseForge", Path.Combine(home, "CurseForge", "minecraft")));
            AddInstances(roots, "PrismLauncher", Path.Combine(appData, "PrismLauncher", "instances"));
            AddInstances(roots, "MultiMC", Path.Combine(appData, "MultiMC", "instances"));
            AddInstances(roots, "GDLauncher", Path.Combine(appData, "gdlauncher_next", "data", "instances"), ".minecraft");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support");
            roots.Add(("Официальный лаунчер", Path.Combine(support, "minecraft")));
            roots.Add(("TLauncher", Path.Combine(support, "tlauncher", "minecraft")));
            roots.Add(("Legacy Launcher", Path.Combine(support, "legacylauncher", ".minecraft")));
            AddInstances(roots, "PrismLauncher", Path.Combine(support, "PrismLauncher", "instances"));
            AddInstances(roots, "MultiMC", Path.Combine(support, "MultiMC", "instances"));
        }
        else
        {
            roots.Add(("Официальный лаунчер", Path.Combine(home, ".minecraft")));
            roots.Add(("TLauncher", Path.Combine(home, ".tlauncher", "minecraft")));
            roots.Add(("Legacy Launcher", Path.Combine(home, ".legacylauncher", ".minecraft")));
            var prism = Path.Combine(home, ".local", "share", "PrismLauncher", "instances");
            AddInstances(roots, "PrismLauncher", prism);
            AddInstances(roots, "PrismLauncher (flathub)", Path.Combine(home, ".var", "app", "org.prismlauncher.PrismLauncher", "data", "prismlauncher", "instances"));
            AddInstances(roots, "MultiMC", Path.Combine(home, ".local", "share", "MultiMC", "instances"));
        }

        return roots;
    }

    private static void AddInstances(List<(string, string)> roots, string label, string instancesDir, string gameSubDir = "minecraft")
    {
        if (!Directory.Exists(instancesDir))
        {
            return;
        }

        try
        {
            foreach (var instance in Directory.EnumerateDirectories(instancesDir))
            {
                var candidate = Path.Combine(instance, gameSubDir);
                if (Directory.Exists(Path.Combine(candidate, "versions")))
                {
                    roots.Add(($"{label} · {Path.GetFileName(instance)}", candidate));
                }
                else if (Directory.Exists(Path.Combine(instance, ".minecraft", "versions")))
                {
                    roots.Add(($"{label} · {Path.GetFileName(instance)}", Path.Combine(instance, ".minecraft")));
                }
            }
        }
        catch (IOException ex)
        {
            // Один испорченный инстанс не должен ломать весь скан.
            _ = ex;
        }
    }

    /// <summary>Версии с валидным <id>.json в versions/.</summary>
    private static List<(string Id, string Directory, long Size)> EnumerateVersions(string root)
    {
        var versionsRoot = Path.Combine(root, "versions");
        var list = new List<(string Id, string Directory, long Size)>();
        if (!Directory.Exists(versionsRoot))
        {
            return list;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(versionsRoot))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id) || !File.Exists(Path.Combine(dir, id + ".json")))
                {
                    continue;
                }

                list.Add((id, dir, Measure(dir, CancellationToken.None)));
            }
        }
        catch (IOException ex)
        {
            _ = ex;
        }

        return list;
    }

    private static long Measure(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // Файл пропал между перечислением и чтением — не критично.
                }
            }

            return total;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public Task<(int Linked, int Copied)> LinkMissingAsync(string destinationMinecraftDirectory, string versionId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var linked = 0;
            var copied = 0;
            var skipped = 0;
            var failed = 0;
            try
            {
                foreach (var (label, root) in CandidateRoots())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    MergeTree(Path.Combine(root, "assets"), Path.Combine(destinationMinecraftDirectory, "assets"), ct,
                        ref linked, ref copied, ref skipped, ref failed);
                    MergeTree(Path.Combine(root, "libraries"), Path.Combine(destinationMinecraftDirectory, "libraries"), ct,
                        ref linked, ref copied, ref skipped, ref failed);
                    if (!string.IsNullOrWhiteSpace(versionId))
                    {
                        MergeTree(Path.Combine(root, "versions", versionId), Path.Combine(destinationMinecraftDirectory, "versions", versionId), ct,
                            ref linked, ref copied, ref skipped, ref failed);
                    }
                }

                if (linked + copied > 0)
                {
                    log.Info("ForeignGameFiles", $"Подтянуто из других лаунчеров перед установкой {versionId}: ссылок {linked}, копий {copied}, пропущено {skipped}, ошибок {failed}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn("ForeignGameFiles", $"Не удалось подтянуть файлы из других лаунчеров: {ex.Message}");
            }

            return (linked, copied);
        }, ct);

    public Task<ForeignImportResult> ImportAsync(
        IReadOnlyList<ForeignVersionInfo> selected,
        string destinationMinecraftDirectory,
        CancellationToken ct = default)
        => Task.Run(() => ImportCore(selected, destinationMinecraftDirectory, ct), ct);

    private ForeignImportResult ImportCore(
        IReadOnlyList<ForeignVersionInfo> selected,
        string destinationMinecraftDirectory,
        CancellationToken ct)
    {
        var result = new ForeignImportResult();
        if (selected is null || selected.Count == 0)
        {
            result.Error = "Ничего не выбрано.";
            result.Summary = result.Error;
            return result;
        }

        try
        {
            Directory.CreateDirectory(destinationMinecraftDirectory);

            var imported = new List<string>();
            var skipped = new List<string>();
            int linked = 0, copied = 0, skippedFiles = 0, failed = 0;

            // 1. Общие папки — один раз на источник, а не на каждую версию.
            foreach (var source in selected.Select(v => v.SourceDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                MergeTree(Path.Combine(source, "libraries"), Path.Combine(destinationMinecraftDirectory, "libraries"), ct,
                    ref linked, ref copied, ref skippedFiles, ref failed);
                MergeTree(Path.Combine(source, "assets"), Path.Combine(destinationMinecraftDirectory, "assets"), ct,
                    ref linked, ref copied, ref skippedFiles, ref failed);
            }

            // 2. Сами версии.
            foreach (var version in selected.DistinctBy(v => v.VersionId))
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(destinationMinecraftDirectory, "versions", version.VersionId);
                if (File.Exists(Path.Combine(dest, version.VersionId + ".json")))
                {
                    skipped.Add(version.VersionId);
                    skippedFiles++;
                    continue;
                }

                MergeTree(version.VersionDirectory, dest, ct,
                    ref linked, ref copied, ref skippedFiles, ref failed);
                imported.Add(version.VersionId);
            }

            result.Success = failed == 0;
            result.VersionsImported = imported.Count;
            result.ImportedVersionIds = imported;
            result.SkippedVersionIds = skipped;
            result.FilesLinked = linked;
            result.FilesCopied = copied;
            result.FilesSkipped = skippedFiles;
            result.FilesFailed = failed;
            result.Summary = $"Версий импортировано: {imported.Count}" +
                             (skipped.Count > 0 ? $" (пропущено уже установленных: {skipped.Count})" : string.Empty) +
                             $". Файлов: {linked + copied} (ссылок: {linked}, копий: {copied}" +
                             (skippedFiles > 0 ? $", пропущено существующих: {skippedFiles}" : string.Empty) +
                             (failed > 0 ? $", ошибок: {failed}" : string.Empty) + ").";

            log.Info("ForeignGameFiles", $"Импорт из других лаунчеров. {result.Summary}");
            return result;
        }
        catch (Exception ex)
        {
            log.Error("ForeignGameFiles", $"Импорт игровых файлов не удался: {ex.Message}", ex);
            result.Error = ex.Message;
            result.Summary = $"Импорт не удался: {ex.Message}";
            return result;
        }
    }

    /// <summary>Слияние дерева: жёсткие ссылки с откатом на копирование; существующие файлы не трогаем.</summary>
    private static void MergeTree(string source, string destination, CancellationToken ct,
        ref int linked, ref int copied, ref int skippedFiles, ref int failed)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            DirectionEnsure(Path.GetDirectoryName(target));

            if (File.Exists(target))
            {
                skippedFiles++;
                continue;
            }

            if (HardLink.TryCreate(target, file))
            {
                linked++;
                continue;
            }

            try
            {
                File.Copy(file, target, overwrite: false);
                copied++;
            }
            catch (IOException)
            {
                failed++;
            }
            catch (UnauthorizedAccessException)
            {
                failed++;
            }
        }
    }

    private static void DirectionEnsure(string? directory)
    {
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
