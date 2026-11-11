using System.IO.Compression;
using EHOT.Launcher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Game.Install;

/// <summary>
/// Распаковка натив-библиотек (LWJGL, JInput и т.д.).
/// Отличие от обычного ZIP: плоская распаковка в одну папку с учётом правил extract.exclude и
/// жёсткий запрет выхода за пределы целевой папки.
/// </summary>
public sealed class NativesExtractor(IPlatformInfo platform, ILogger<NativesExtractor> logger)
{
    private readonly HashSet<string> _extracted = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> ExtractedFiles => _extracted;

    public async Task ExtractAsync(
        string jarPath,
        string destinationDirectory,
        IEnumerable<string>? excludedPrefixes = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var excluded = (excludedPrefixes ?? [])
            .Select(p => p.Replace('\\', '/').TrimStart('/'))
            .Where(p => p.Length > 0)
            .ToArray();

        var rootFull = Path.GetFullPath(destinationDirectory);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var comparison = platform.IsWindows || platform.IsMacOS
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        await using var stream = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var name = entry.FullName.Replace('\\', '/');
            if (excluded.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Нативы в JAR лежат плоско: берём только имя файла.
            var fileName = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(rootFull, fileName));
            if (!target.StartsWith(rootFull, comparison))
            {
                logger.LogWarning("Пропущена подозрительная запись натива: {Name}", entry.FullName);
                continue;
            }

            await using var entryStream = entry.Open();
            await using var outStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await entryStream.CopyToAsync(outStream, ct).ConfigureAwait(false);
            _extracted.Add(fileName);
        }

        logger.LogInformation("Нативы распакованы в {Dir} ({Count} файлов)", destinationDirectory, _extracted.Count);
    }
}
