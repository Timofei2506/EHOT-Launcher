using System.Security.Cryptography;
using System.Text;

namespace EHOT.Launcher.Core.Security;

/// <summary>
/// Защита путей: любые операции с файлами, приходящими извне (архивы, API, пользовательский ввод),
/// обязаны проходить через этот класс.
/// </summary>
public static class PathGuard
{
    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Проверяет, что <paramref name="candidate"/> лежит внутри <paramref name="root"/>.
    /// Возвращает нормализованный абсолютный путь.
    /// </summary>
    /// <exception cref="SecurityException">Путь выходит за пределы корня или содержит недопустимые элементы.</exception>
    public static string EnsureInside(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var combined = Path.IsPathRooted(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(Path.Combine(rootFull, candidate));

        if (!combined.StartsWith(rootFull, PathComparison))
        {
            throw new SecurityException($"Путь '{candidate}' выходит за пределы корня '{root}'.");
        }

        return combined;
    }

    public static bool IsInside(string root, string candidate)
    {
        try
        {
            EnsureInside(root, candidate);
            return true;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Проверяет относительный путь из архива/манифеста: без корней, без .., без пустых сегментов,
    /// без зарезервированных имён Windows, без управляющих символов, длина сегмента ≤ 255.
    /// </summary>
    public static string SanitizeRelativeEntryPath(string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);

        var normalized = entryPath.Replace('\\', '/').Trim();

        if (normalized.Length == 0)
        {
            throw new SecurityException("Пустой путь записи архива.");
        }

        if (normalized.StartsWith('/') || normalized.StartsWith('~') || normalized.Contains(':'))
        {
            throw new SecurityException($"Недопустимый путь записи архива: '{entryPath}'.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new SecurityException("Путь записи содержит управляющие символы.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new SecurityException("Путь записи не содержит сегментов.");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new SecurityException($"Path traversal в записи архива: '{entryPath}'.");
            }

            if (segment.Length > 255)
            {
                throw new SecurityException("Слишком длинный сегмент пути.");
            }

            var stem = segment.Split('.')[0];
            if (ReservedWindowsNames.Contains(stem))
            {
                throw new SecurityException($"Зарезервированное имя Windows в пути: '{segment}'.");
            }

            if (segment.IndexOfAny(InvalidNameChars) >= 0)
            {
                throw new SecurityException($"Недопустимые символы в имени: '{segment}'.");
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Безопасное имя файла из произвольной строки (ник, название проекта).
    /// Никогда не возвращает пустую строку — использует fallback.
    /// </summary>
    public static string ToSafeFileName(string input, string fallback = "file", int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' '
                ? ch
                : '_');
        }

        var name = sb.ToString().Trim().Trim('.');
        if (name.Length == 0)
        {
            return fallback;
        }

        if (name.Length > maxLength)
        {
            name = name[..maxLength];
        }

        var stem = name.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem))
        {
            name = "_" + name;
        }

        return name;
    }

    /// <summary>Корректный ник Minecraft (3–16 символов, буквы/цифры/подчёркивание).</summary>
    public static bool IsValidMinecraftUsername(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length is >= 3 and <= 16
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Детерминированный оффлайн-UUID (не выдаётся за официальный UUID аккаунта).</summary>
    public static Guid OfflineUuidFor(string username)
    {
        // Официальное offline-поведение Java-клиента: UUID v3 от "OfflinePlayer:<name>".
        // MD5 здесь не является средством защиты — это часть формата, совместимого с ванильным сервером.
#pragma warning disable CA5351
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username.Trim()));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC 4122
#pragma warning restore CA5351
        return new Guid(
            BitConverter.ToInt32(bytes, 0),
            BitConverter.ToInt16(bytes, 4),
            BitConverter.ToInt16(bytes, 6),
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Создаёт каталог с длинным путём-совместимостью на Windows.</summary>
    public static string CreateDirectorySafe(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full.Length > 240 && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            full = @"\\?\" + full;
        }

        Directory.CreateDirectory(full);
        return full;
    }
}

/// <summary>Исключение безопасности (не путать с системным SecurityException).</summary>
public sealed class SecurityException : Exception
{
    public SecurityException(string message) : base(message)
    {
    }

    public SecurityException(string message, Exception inner) : base(message, inner)
    {
    }
}
