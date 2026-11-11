using System.Formats.Tar;
using System.IO.Compression;
using EHOT.Launcher.Core.Security;

namespace EHOT.Launcher.Game.Install;

/// <summary>
/// Распаковка .tar.gz (Adoptium отдаёт JRE в этом формате для macOS/Linux).
/// Проверка путей та же, что и для ZIP: никаких выходов за пределы каталога и ссылок.
/// </summary>
public static class TarArchive
{
    public static bool ExtractAsync(string archivePath, string destinationDirectory, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var destRoot = Path.GetFullPath(destinationDirectory);

            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    continue; // ссылки не распаковываем
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                {
                    continue;
                }

                var relative = PathGuard.SanitizeRelativeEntryPath(entry.Name);
                var target = PathGuard.EnsureInside(destRoot, relative.Replace('/', Path.DirectorySeparatorChar));

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(target, overwrite: true);

                if (!OperatingSystem.IsWindows()
                    && entry.Mode.HasFlag(UnixFileMode.UserExecute)
                    && Path.GetFileName(entry.Name) is "java" or "java.exe")
                {
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
