using System.Text.RegularExpressions;

namespace EHOT.Launcher.UI.Services;

/// <summary>Разобранная ссылка `ehot://…`.</summary>
public sealed partial record DeepLink(DeepLinkKind Kind, string? JoinCode = null, string? RoomName = null, string? FilePath = null)
{
    public static DeepLink? None => null;
}

public enum DeepLinkKind
{
    /// <summary>Приглашение в комнату: ehot://join/ABCD2345</summary>
    Join,

    /// <summary>Прямая ссылка на комнату: ehot://room/ABCD2345?name=Игра</summary>
    Room,

    /// <summary>Импорт модпака: ehot://install?file=C:\path\pack.mrpack или ehot://modpack/https://…</summary>
    Install
}

/// <summary>
/// Разбор внешних ссылок и аргументов командной строки. Единственное место, где решается,
/// что делать с `ehot://…`: экраны и навигация не должны знать про форматы ссылок.
///
/// Поддерживаемые формы:
///   ehot://join/ABCD2345
///   ehot://join?code=ABCD2345
///   ehot://room/ABCD2345?name=Вечерняя%20игра
///   ehot://install?file=%2Fhome%2Fuser%2Fpack.mrpack
///   ehot://modpack/https%3A%2F%2Fexample.com%2Fpack.mrpack
///   а также «голый» код из 8 символов, переданный аргументом: EHOT.Launcher.exe ABCD2345
/// </summary>
public static partial class DeepLinkParser
{
    /// <summary>Код приглашения: 8 символов из алфавита без похожих 0/O/1/I.</summary>
    [GeneratedRegex(@"^[A-HJ-NP-Z2-9]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static bool IsValidCode(string? value) => value is not null && CodePattern().IsMatch(value.Trim());

    /// <summary>Разбирает ссылку или аргумент командной строки. Неизвестное — возвращает null (без исключений).</summary>
    public static DeepLink? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().Trim('"');

        // Аргумент — просто код приглашения (например, из ярлыка или скрипта).
        if (IsValidCode(value))
        {
            return new DeepLink(DeepLinkKind.Join, value.ToUpperInvariant());
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, "ehot", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // ehot://join/CODE — хост = действие, первый сегмент пути = код.
        var action = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(uri.Query);

        switch (action)
        {
            case "join":
            {
                var code = segments.Length > 0 ? segments[0] : query.GetValueOrDefault("code");
                return IsValidCode(code)
                    ? new DeepLink(DeepLinkKind.Join, code!.ToUpperInvariant())
                    : null;
            }

            case "room":
            {
                var code = segments.Length > 0 ? segments[0] : query.GetValueOrDefault("code");
                return IsValidCode(code)
                    ? new DeepLink(DeepLinkKind.Room, code!.ToUpperInvariant(), query.GetValueOrDefault("name"))
                    : null;
            }

            case "install":
            case "modpack":
            {
                // Путь может быть локальным файлом или https-ссылкой.
                var file = query.GetValueOrDefault("file");
                if (string.IsNullOrWhiteSpace(file) && segments.Length > 0)
                {
                    file = Uri.UnescapeDataString(string.Join('/', segments));
                }

                if (string.IsNullOrWhiteSpace(file))
                {
                    return null;
                }

                return new DeepLink(DeepLinkKind.Install, FilePath: file);
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
