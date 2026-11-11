using System.IO.Compression;
using EHOT.Launcher.Core.Security;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Core.Archives;

public sealed record ExtractionLimits
{
    public long MaxUncompressedBytes { get; init; } = 4L * 1024 * 1024 * 1024; // 4 GiB
    public int MaxEntries { get; init; } = 50_000;
    public long MaxRatio { get; init; } = 200; // защита от zip-бомб
    public long MaxEntryBytes { get; init; } = 1L * 1024 * 1024 * 1024; // 1 GiB
    public bool AllowOverwrite { get; init; } = true;

    public static ExtractionLimits Default { get; } = new();

    /// <summary>Строгие ограничения для модов/паков (содержимое не должно быть гигантским).</summary>
    public static ExtractionLimits ContentPack { get; } = new()
    {
        MaxUncompressedBytes = 2L * 1024 * 1024 * 1024,
        MaxEntries = 20_000,
        MaxRatio = 150,
        MaxEntryBytes = 256L * 1024 * 1024
    };
}

public sealed record ExtractionReport(int Entries, long UncompressedBytes);

/// <summary>
/// Безопасная распаковка ZIP: zip-slip, абсолютные пути, симлинки, zip-бомбы,
/// переполнение по количеству записей. Любая аномалия — ошибка, а не «пропустим».
/// </summary>
public interface IArchiveExtractor
{
    Task<ExtractionReport> ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        ExtractionLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    Task<ExtractionReport> ExtractZipAsync(
        Stream archiveStream,
        string destinationDirectory,
        ExtractionLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>Читает один текстовый файл из ZIP без распаковки на диск (например, modrinth.index.json).</summary>
    Task<string?> ReadTextEntryAsync(string archivePath, string entryName, int maxBytes = 8 * 1024 * 1024, CancellationToken ct = default);
}

public sealed class SafeZipExtractor(ILogger<SafeZipExtractor> logger) : IArchiveExtractor
{
    public Task<ExtractionReport> ExtractZipAsync(
        string archivePath,
        string destinationDirectory,
        ExtractionLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return await ExtractCoreAsync(fs, destinationDirectory, limits ?? ExtractionLimits.Default, progress, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<ExtractionReport> ExtractZipAsync(
        Stream archiveStream,
        string destinationDirectory,
        ExtractionLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => ExtractCoreAsync(archiveStream, destinationDirectory, limits ?? ExtractionLimits.Default, progress, ct);

    private async Task<ExtractionReport> ExtractCoreAsync(
        Stream stream,
        string destinationDirectory,
        ExtractionLimits limits,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destRoot = Path.GetFullPath(destinationDirectory);
        var totalCompressed = stream.CanSeek ? stream.Length : 0;
        long written = 0;
        var entries = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        // Предварительная проверка: суммарный размер и количество записей.
        if (zip.Entries.Count > limits.MaxEntries)
        {
            throw new SecurityException($"Архив содержит {zip.Entries.Count} записей, лимит {limits.MaxEntries}.");
        }

        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.FullName.EndsWith('/'))
            {
                continue;
            }

            if (e.Length > limits.MaxEntryBytes)
            {
                throw new SecurityException($"Запись '{e.FullName}' слишком велика ({e.Length} байт).");
            }

            written += e.Length;
            if (written > limits.MaxUncompressedBytes)
            {
                throw new SecurityException("Превышен лимит распакованного объёма архива.");
            }

            if (e.CompressedLength > 0 && e.Length > 1024 * 1024 && e.Length / Math.Max(1, e.CompressedLength) > limits.MaxRatio)
            {
                throw new SecurityException($"Подозрительная степень сжатия в '{e.FullName}' (возможна zip-бомба).");
            }
        }

        written = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue; // директория
            }

            // Симлинки в ZIP кодируются внешними атрибутами; запрещаем полностью.
            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((unixMode & 0xF000) == 0xA000)
            {
                throw new SecurityException($"Символическая ссылка в архиве запрещена: '{entry.FullName}'.");
            }

            var safeRelative = PathGuard.SanitizeRelativeEntryPath(entry.FullName);
            var targetPath = PathGuard.EnsureInside(destRoot, safeRelative.Replace('/', Path.DirectorySeparatorChar));

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!limits.AllowOverwrite && File.Exists(targetPath))
            {
                throw new SecurityException($"Файл уже существует: '{safeRelative}'.");
            }

            await using (var entryStream = entry.Open())
            await using (var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await entryStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;

                    if (written > limits.MaxUncompressedBytes)
                    {
                        throw new SecurityException("Превышен лимит распакованного объёма архива во время записи.");
                    }
                }
            }

            entries++;
            if (progress is not null && totalCompressed > 0)
            {
                progress.Report(0);
            }

            if (entries % 25 == 0)
            {
                logger.LogDebug("Распаковано {Entries} записей в {Dir}", entries, destinationDirectory);
            }
        }

        progress?.Report(1);
        logger.LogInformation("Архив распакован: {Entries} файлов, {Bytes} байт → {Dir}", entries, written, destinationDirectory);
        return new ExtractionReport(entries, written);
    }

    public async Task<string?> ReadTextEntryAsync(string archivePath, string entryName, int maxBytes = 8 * 1024 * 1024, CancellationToken ct = default)
    {
        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        if (entry.Length > maxBytes)
        {
            throw new SecurityException($"Запись '{entryName}' превышает допустимый размер для чтения в память.");
        }

        await using var es = entry.Open();
        using var reader = new StreamReader(es);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
