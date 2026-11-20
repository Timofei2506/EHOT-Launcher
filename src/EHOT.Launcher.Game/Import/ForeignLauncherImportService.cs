using System.Text.Json;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Game.Profiles;

namespace EHOT.Launcher.Game.Import;

/// <summary>Перенос пользовательского контента из каталога другого лаунчера (<c>.minecraft</c>) в наш инстанс.</summary>
public sealed class ForeignLauncherImportService : IForeignLauncherImportService
{
    private const string SavesDir = "saves";
    private const string ScreenshotsDir = "screenshots";
    private const string ResourcePacksDir = "resourcepacks";
    private const string ModsDir = "mods";

    private readonly IProfileManager _profiles;
    private readonly IAppPaths _paths;
    private readonly ILogService _log;

    public ForeignLauncherImportService(
        IProfileManager profiles,
        IAppPaths paths,
        ILogService log)
    {
        _profiles = profiles;
        _paths = paths;
        _log = log;
    }

    public async Task<ImportScanResult> ScanAsync(string minecraftDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new ImportScanResult { SourceDirectory = minecraftDirectory };

        if (string.IsNullOrWhiteSpace(minecraftDirectory) || !Directory.Exists(minecraftDirectory))
        {
            result.Summary = "Каталог не найден. Укажите существующую папку .minecraft другого лаунчера.";
            return result;
        }

        var saves = CountDirectories(Path.Combine(minecraftDirectory, SavesDir));
        var screenshots = CountFiles(Path.Combine(minecraftDirectory, ScreenshotsDir), "*.png");
        var resourcePacks = CountEntries(Path.Combine(minecraftDirectory, ResourcePacksDir));
        var mods = CountFiles(Path.Combine(minecraftDirectory, ModsDir), "*.jar");

        result.Importable = new ImportableContent
        {
            Saves = saves,
            Screenshots = screenshots,
            ResourcePacks = resourcePacks,
            Mods = mods,
        };

        var skipped = new List<SkippedItem>();

        if (Directory.Exists(Path.Combine(minecraftDirectory, "versions")))
            skipped.Add(new SkippedItem { Path = "versions/", Reason = "Версии игры относятся к исходному лаунчеру — не копируются." });
        if (Directory.Exists(Path.Combine(minecraftDirectory, "libraries")))
            skipped.Add(new SkippedItem { Path = "libraries/", Reason = "Библиотеки исходного лаунчера не копируются." });
        if (File.Exists(Path.Combine(minecraftDirectory, "launcher_profiles.json")))
            skipped.Add(new SkippedItem { Path = "launcher_profiles.json", Reason = "Профили и данные входа исходного лаунчера не копируются." });
        if (File.Exists(Path.Combine(minecraftDirectory, "usercache.json")))
            skipped.Add(new SkippedItem { Path = "usercache.json", Reason = "Кэш учётных записей исходного лаунчера не копируется." });
        if (mods > 0)
            skipped.Add(new SkippedItem { Path = "mods/", Reason = "Моды не копируются автоматически: их лоадер может не совпадать с нашим. Перенесите их вручную, если нужно." });
        if (Directory.Exists(Path.Combine(minecraftDirectory, "config")))
            skipped.Add(new SkippedItem { Path = "config/", Reason = "Конфиги исходного лаунчера не копируются." });

        result.Skipped = skipped;

        var looksLike = saves > 0 || screenshots > 0 || resourcePacks > 0 || mods > 0 ||
                        File.Exists(Path.Combine(minecraftDirectory, "options.txt")) ||
                        File.Exists(Path.Combine(minecraftDirectory, "version_manifest.json"));
        result.LooksLikeMinecraft = looksLike;

        var detected = DetectLastVersion(minecraftDirectory);
        result.DetectedVersion = detected;

        var parts = new List<string>();
        if (saves > 0) parts.Add($"{saves} миров");
        if (screenshots > 0) parts.Add($"{screenshots} скриншотов");
        if (resourcePacks > 0) parts.Add($"{resourcePacks} пакетов ресурсов");
        result.Summary = parts.Count > 0
            ? $"Найдено к переносу: {string.Join(", ", parts)}."
            : "Подходящего пользовательского контента не найдено.";

        _log.Info("ForeignLauncherImport", $"Сканирование {minecraftDirectory}: {result.Summary}");
        return result;
    }

    public async Task<ImportResult> ImportAsync(
        string sourceDirectory,
        string instanceName,
        string minecraftVersion,
        ImportSelection selection,
        CancellationToken ct = default)
    {
        var result = new ImportResult();

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            result.Error = "Введите название импортируемого инстанса.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            result.Error = "Укажите существующую папку .minecraft.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            result.Error = "Укажите версию Minecraft для инстанса.";
            return result;
        }

        try
        {
            var scan = await ScanAsync(sourceDirectory, ct).ConfigureAwait(false);
            if (!scan.CanImport && !selection.Mods)
            {
                result.Error = "Нечего импортировать: в выбранной папке нет миров, скриншотов или пакетов ресурсов.";
                return result;
            }

            var create = await _profiles.CreateAsync(new CreateProfileRequest
            {
                Name = instanceName.Trim(),
                MinecraftVersion = minecraftVersion.Trim(),
                Loader = LoaderKind.Vanilla,
                BaseDirectory = _paths.AppDataDirectory,
            }, ct).ConfigureAwait(false);

            if (!create.IsSuccess)
            {
                result.Error = create.Message ?? "Не удалось создать инстанс.";
                return result;
            }

            var profile = create.Value!;
            var instanceDir = profile.InstanceDirectory;
            var copied = new List<CopiedItem>();
            int skipped = 0;

            if (selection.Saves && Directory.Exists(Path.Combine(sourceDirectory, SavesDir)))
            {
                var n = CopyDirectory(Path.Combine(sourceDirectory, SavesDir), Path.Combine(instanceDir, SavesDir), ct);
                copied.Add(new CopiedItem { Kind = "миров", Count = n, Destination = Path.Combine(instanceDir, SavesDir) });
                skipped += n;
            }

            if (selection.Screenshots && Directory.Exists(Path.Combine(sourceDirectory, ScreenshotsDir)))
            {
                var n = CopyDirectory(Path.Combine(sourceDirectory, ScreenshotsDir), Path.Combine(instanceDir, "screenshots"), ct);
                copied.Add(new CopiedItem { Kind = "скриншотов", Count = n, Destination = Path.Combine(instanceDir, "screenshots") });
                skipped += n;
            }

            if (selection.ResourcePacks && Directory.Exists(Path.Combine(sourceDirectory, ResourcePacksDir)))
            {
                var n = CopyDirectory(Path.Combine(sourceDirectory, ResourcePacksDir), Path.Combine(instanceDir, ResourcePacksDir), ct);
                copied.Add(new CopiedItem { Kind = "пакетов ресурсов", Count = n, Destination = Path.Combine(instanceDir, ResourcePacksDir) });
                skipped += n;
            }

            if (selection.Mods && Directory.Exists(Path.Combine(sourceDirectory, ModsDir)))
            {
                var n = CopyDirectory(Path.Combine(sourceDirectory, ModsDir), Path.Combine(instanceDir, ModsDir), ct);
                copied.Add(new CopiedItem { Kind = "модов", Count = n, Destination = Path.Combine(instanceDir, ModsDir) });
                skipped += n;
            }

            result.Success = true;
            result.InstanceName = profile.Name;
            result.InstanceDirectory = instanceDir;
            result.Copied = copied;
            result.Summary = copied.Count > 0
                ? $"Инстанс «{profile.Name}» создан. Перенесено: {string.Join(", ", copied.Select(c => $"{c.Count} {c.Kind}"))}."
                : $"Инстанс «{profile.Name}» создан, переносить нечего.";
            result.SkippedCount = scan.Skipped.Count;

            _log.Info("ForeignLauncherImport", $"Импорт из {sourceDirectory} → {instanceDir}: {result.Summary}");
            return result;
        }
        catch (Exception ex)
        {
            _log.Error("ForeignLauncherImport", $"Импорт не удался: {ex.Message}", ex);
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }

    private static int CountDirectories(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Count() : 0;

    private static int CountFiles(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Count() : 0;

    private static int CountEntries(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;

    private static int CopyDirectory(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }

    private static string? DetectLastVersion(string minecraftDirectory)
    {
        try
        {
            var profiles = Path.Combine(minecraftDirectory, "launcher_profiles.json");
            if (!File.Exists(profiles)) return null;
            var json = File.ReadAllText(profiles);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("profiles", out var profilesElem) &&
                profilesElem.ValueKind == JsonValueKind.Object)
            {
                string? best = null;
                foreach (var prop in profilesElem.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("lastVersionId", out var ver) && ver.ValueKind == JsonValueKind.String)
                    {
                        var v = ver.GetString();
                        if (!string.IsNullOrEmpty(v)) best = v;
                    }
                }
                return best;
            }
        }
        catch
        {
            // Ignore — it's a hint, not a requirement.
        }
        return null;
    }
}
