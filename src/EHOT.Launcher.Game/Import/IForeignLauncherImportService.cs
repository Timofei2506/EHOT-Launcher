using System.Threading;
using System.Threading.Tasks;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Models;

namespace EHOT.Launcher.Game.Import;

/// <summary>Контент, который можно перенести из чужого каталога <c>.minecraft</c>.</summary>
public sealed class ImportableContent
{
    public int Saves { get; set; }
    public int Screenshots { get; set; }
    public int ResourcePacks { get; set; }
    public int Mods { get; set; }
    public int TotalCount => Saves + Screenshots + ResourcePacks;
}

/// <summary>Пропущенная папка/файл с честной причиной.</summary>
public sealed class SkippedItem
{
    public required string Path { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Выбор того, что импортировать.</summary>
public sealed class ImportSelection
{
    public bool Saves { get; set; } = true;
    public bool Screenshots { get; set; } = true;
    public bool ResourcePacks { get; set; } = true;
    public bool Mods { get; set; } = false; // по умолчанию не трогаем моды — их лоадер может не совпадать
}

/// <summary>Один перенесённый набор файлов.</summary>
public sealed class CopiedItem
{
    public required string Kind { get; init; }
    public required int Count { get; init; }
    public required string Destination { get; init; }
}

/// <summary>Результат сканирования чужого каталога <c>.minecraft</c>.</summary>
public sealed class ImportScanResult
{
    public required string SourceDirectory { get; init; }
    public bool LooksLikeMinecraft { get; set; }
    public ImportableContent Importable { get; set; } = new();
    public IReadOnlyList<SkippedItem> Skipped { get; set; } = [];
    public string? DetectedVersion { get; set; } // последняя версия из профиля исходного лаунчера, если нашлась
    public string Summary { get; set; } = string.Empty;
    public bool CanImport => LooksLikeMinecraft && Importable.TotalCount > 0;
}

/// <summary>Результат импорта.</summary>
public sealed class ImportResult
{
    public bool Success { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string InstanceDirectory { get; set; } = string.Empty;
    public IReadOnlyList<CopiedItem> Copied { get; set; } = [];
    public int SkippedCount { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = string.Empty;
}

/// <summary>Импорт пользовательского контента из каталога другого лаунчера (<c>.minecraft</c>).
///
/// Честность: мы никогда не читаем чужой %APPDATA%\.minecraft автоматически — путь должен
/// указать пользователь. Переносим только пользовательский контент (миры, скриншоты,
/// пакеты ресурсов). Версии, библиотеки, логин-данные и конфиги исходного лаунчера не
/// трогаем — они относятся к исходному лаунчеру.
/// </summary>
public interface IForeignLauncherImportService
{
    Task<ImportScanResult> ScanAsync(string minecraftDirectory, CancellationToken ct = default);

    Task<ImportResult> ImportAsync(
        string sourceDirectory,
        string instanceName,
        string minecraftVersion,
        ImportSelection selection,
        CancellationToken ct = default);
}
