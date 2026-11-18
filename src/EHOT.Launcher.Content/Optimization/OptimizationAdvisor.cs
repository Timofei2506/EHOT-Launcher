using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Platform.Shared;

namespace EHOT.Launcher.Content.Optimization;

/// <summary>Почему мод рекомендован именно этому компьютеру.</summary>
public sealed record OptimizationRecommendation(
    string ProjectId,
    string Title,
    string Slug,
    string Reason,
    string IconUrl)
{
    /// <summary>Готовая версия файла под выбранную сборку — её и ставим.</summary>
    public ContentVersion? Version { get; init; }

    /// <summary>Нужен ли мод именно на слабом ПК (на мощном он не обязателен).</summary>
    public bool LowEndOnly { get; init; }

    public string VersionLabel => Version is null
        ? "нет сборки под эту версию"
        : $"v{Version.VersionNumber}";
}

/// <summary>Итог подбора: рекомендации плюс честное описание железа, по которому они сделаны.</summary>
public sealed record OptimizationPlan(
    GraphicsInfo Graphics,
    long MemoryBytes,
    int Cores,
    IReadOnlyList<OptimizationRecommendation> Recommendations,
    string Summary,
    bool LowEnd);

/// <summary>
/// Подбор модов оптимизации под конкретный компьютер.
/// <para>
/// Сначала читаем реальное железо (видеокарта, ОЗУ, ядра), затем берём из каталога Modrinth
/// только те моды, у которых есть файл под выбранную версию игры и загрузчик.
/// Слабый ПК получает полный набор (Sodium/Embeddium, EntityCulling, Indium),
/// мощный — только то, что не влияет на картинку (Lithium, FerriteCore, Krypton).
/// Если железо определить не удалось, это прямо написано в описании плана.
/// </para>
/// </summary>
public sealed class OptimizationAdvisor(
    IContentProviderRegistry providers,
    IGraphicsDetector graphics,
    IPlatformInfo platform,
    ILogService log)
{
    /// <summary>
    /// Проверенные моды оптимизации: только то, что реально работает и не ломает игру.
    /// Fabric и Forge/NeoForge используют разные моды одного назначения.
    /// </summary>
    private static readonly (string Slug, string Title, string Reason, bool LowEndOnly, bool FabricOnly, bool ForgeOnly)[] Known =
    [
        ("sodium", "Sodium", "Переписанный рендер: в разы больше FPS", true, true, false),
        ("indium", "Indium", "Совместимость Sodium с модами на Fabric Rendering API", true, true, false),
        ("embeddium", "Embeddium", "Рендер-оптимизация для Forge (аналог Sodium)", true, false, true),
        ("entityculling", "Entity Culling", "Не рисует невидимое — заметный плюс на слабых ПК", true, false, false),
        ("lithium", "Lithium", "Оптимизация тиков, физики и ИИ без потери контента", false, true, false),
        ("ferrite-core", "FerriteCore", "Экономия ОЗУ при том же мире", false, false, false),
        ("krypton", "Krypton", "Сетевой стек: меньше лагов на серверах", false, true, false),
        ("modernfix", "ModernFix", "Быстрее загрузка и меньше памяти (Forge/NeoForge)", false, false, true)
    ];

    public async Task<OperationResult<OptimizationPlan>> BuildPlanAsync(
        string minecraftVersion,
        LoaderKind loader,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return OperationResult<OptimizationPlan>.Fail(ErrorCode.Validation, "Не указана версия Minecraft.");
        }

        if (loader is LoaderKind.Vanilla)
        {
            return OperationResult<OptimizationPlan>.Fail(ErrorCode.Validation,
                "Модам нужен загрузчик: выберите Fabric (Sodium) или Forge/NeoForge (Embeddium).");
        }

        var provider = providers.Get("modrinth") ?? providers.Primary;
        if (!provider.IsAvailable)
        {
            return OperationResult<OptimizationPlan>.Fail(ErrorCode.Network,
                $"Каталог Modrinth недоступен: {provider.UnavailableReason ?? "нет соединения"}");
        }

        var info = await graphics.DetectAsync(ct).ConfigureAwait(false);
        var hardware = new DeviceProfile(platform.TotalMemoryBytes, platform.LogicalProcessorCount);

        var fabric = loader is LoaderKind.Fabric or LoaderKind.Quilt;
        // Низкий класс = слабая графика или мало памяти/ядер. При неизвестной видеокарте
        // опираемся только на ОЗУ и ядра, чтобы не навязывать лишние моды.
        var lowEnd = info.Tier switch
        {
            GraphicsTier.Low => true,
            GraphicsTier.High => false,
            _ => hardware.MemoryBytes is > 0 and < 12L * 1024 * 1024 * 1024 || hardware.Cores <= 4
        };

        var picked = new List<OptimizationRecommendation>();
        var skipped = new List<string>();

        foreach (var candidate in Known)
        {
            if (fabric && candidate.ForgeOnly)
            {
                continue;
            }

            if (!fabric && candidate.FabricOnly)
            {
                continue;
            }

            if (candidate.LowEndOnly && !lowEnd)
            {
                log.Info("Optimization", $"{candidate.Title}: не нужен на этом железе.");
                continue;
            }

            var project = await provider.GetProjectAsync(candidate.Slug, ct).ConfigureAwait(false);
            if (project is null)
            {
                skipped.Add(candidate.Title);
                continue;
            }

            var versions = await provider.GetVersionsAsync(project.Id, minecraftVersion, loader, ct)
                .ConfigureAwait(false);
            var version = versions.FirstOrDefault(v => v.PrimaryFile is not null);
            if (version is null)
            {
                log.Info("Optimization", $"{candidate.Title}: нет файла под {minecraftVersion}/{loader}.");
                skipped.Add(candidate.Title);
                continue;
            }

            picked.Add(new OptimizationRecommendation(project.Id, candidate.Title, candidate.Slug,
                candidate.Reason, project.IconUrl ?? string.Empty)
            {
                Version = version,
                LowEndOnly = candidate.LowEndOnly
            });
        }

        if (picked.Count == 0)
        {
            return OperationResult<OptimizationPlan>.Fail(ErrorCode.NotFound,
                $"Под Minecraft {minecraftVersion} ({loader}) в каталоге нет подходящих модов оптимизации.");
        }

        var summary = Describe(info, hardware, lowEnd);
        if (skipped.Count > 0)
        {
            summary += $" Не найдены под эту версию: {string.Join(", ", skipped)}.";
        }

        return OperationResult<OptimizationPlan>.Ok(
            new OptimizationPlan(info, hardware.MemoryBytes, hardware.Cores, picked, summary, lowEnd));
    }

    private static string Describe(GraphicsInfo gpu, DeviceProfile hardware, bool lowEnd)
    {
        var gpuText = gpu.Tier == GraphicsTier.Unknown
            ? "видеокарта не определена"
            : $"{gpu.Name} · {gpu.VramLabel}";
        var ram = hardware.MemoryBytes > 0
            ? $"{(hardware.MemoryBytes / (1024d * 1024 * 1024)):0} ГБ ОЗУ"
            : "ОЗУ неизвестно";
        var verdict = lowEnd
            ? "Слабый ПК — ставим всё, что поднимает FPS."
            : "Машина тянет — берём только то, что не меняет картинку.";
        return $"{gpuText} · {ram} · {hardware.Cores} ядер. {verdict}";
    }
}

/// <summary>Данные об устройстве, которые не зависят от видеокарты.</summary>
public sealed record DeviceProfile(long MemoryBytes, int Cores);
