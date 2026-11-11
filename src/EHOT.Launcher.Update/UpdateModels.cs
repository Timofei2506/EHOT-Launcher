namespace EHOT.Launcher.Update;

/// <summary>Один файл релиза, который умеет отдавать GitHub Releases API.</summary>
public sealed record ReleaseAsset(
    string Name,
    Uri DownloadUrl,
    long SizeBytes,
    long Id);

/// <summary>Информация о релизе, приведённая к тому, что нужно лаунчеру.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string TagName,
    string Title,
    string? Notes,
    Uri HtmlUrl,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets,
    string? Sha256 = null);

/// <summary>Результат проверки обновлений.</summary>
public enum UpdateAvailability
{
    /// <summary>Обновлений нет — установлена последняя версия.</summary>
    UpToDate = 0,

    /// <summary>Доступна более новая версия.</summary>
    Available = 1,

    /// <summary>Проверка не удалась (нет сети, сервер молчит, формат ответа не тот).</summary>
    Failed = 2,

    /// <summary>В сборке нет канала обновлений (например, запуск из исходников).</summary>
    NotConfigured = 3
}

public sealed record UpdateCheckResult(
    UpdateAvailability Status,
    ReleaseInfo? Release = null,
    Version? CurrentVersion = null,
    string? Message = null)
{
    public bool HasUpdate => Status == UpdateAvailability.Available && Release is not null;
}

/// <summary>Какой файл и куда скачивать под текущую платформу.</summary>
public sealed record UpdatePlan(
    ReleaseInfo Release,
    ReleaseAsset Asset,
    string TargetPath,
    string StagingDirectory);

public enum UpdateStage
{
    Idle = 0,
    Checking = 1,
    Downloading = 2,
    Verifying = 3,
    Ready = 4,
    Applying = 5,
    Failed = 6
}

public sealed record UpdateProgress(UpdateStage Stage, double Fraction, string Message);

/// <summary>
/// Проверка и установка обновлений. Реализация не имитирует успех: если канал не настроен
/// или сеть недоступна, возвращается честный статус, а не «обновлений нет».
/// </summary>
public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>Канал обновлений настроен (есть репозиторий и не отключено настройкой).</summary>
    bool IsConfigured { get; }

    /// <summary>Проверяет последний релиз в канале. force — проверить даже если авто-проверка выключена.</summary>
    Task<UpdateCheckResult> CheckAsync(bool includePrerelease = true, CancellationToken ct = default);

    /// <summary>Скачивает нужный файл во временную папку и проверяет размер.</summary>
    Task<UpdatePlan> DownloadAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Запускает установку: подменяет файлы при выходе приложения и перезапускает его.</summary>
    Task ApplyAsync(UpdatePlan plan, CancellationToken ct = default);
}
