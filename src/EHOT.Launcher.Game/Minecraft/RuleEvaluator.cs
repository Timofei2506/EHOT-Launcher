using System.Text.RegularExpressions;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Game.Minecraft;

namespace EHOT.Launcher.Game.Minecraft;

public sealed record RuleContext(
    string OsName,
    string OsVersion,
    string Arch,
    IReadOnlyDictionary<string, bool> Features)
{
    public static RuleContext FromPlatform(IPlatformInfo platform, IReadOnlyDictionary<string, bool>? features = null)
    {
        var os = platform.IsWindows ? "windows" : platform.IsMacOS ? "osx" : "linux";
        var arch = platform.Architecture switch
        {
            "x64" => "x86_64",
            "arm64" => "arm64",
            "x86" => "x86",
            _ => platform.Architecture
        };

        return new RuleContext(
            os,
            platform.OsVersion,
            arch,
            features ?? new Dictionary<string, bool>());
    }
}

/// <summary>
/// Движок правил Mojang: пустой список правил = разрешено;
/// иначе побеждает первое подходящее правило, по умолчанию — запрещено.
/// </summary>
public static partial class RuleEvaluator
{
    [GeneratedRegex(@"^(?<op>\^|~|>=|<=|>|<|=)?(?<ver>[\d.]+)$")]
    private static partial Regex VersionPattern();

    public static bool IsAllowed(IReadOnlyList<RuleDto>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        foreach (var rule in rules)
        {
            if (Matches(rule, context))
            {
                return string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public static bool Matches(RuleDto rule, RuleContext context)
    {
        if (rule.Os is not null)
        {
            if (!string.IsNullOrEmpty(rule.Os.Name) && !string.Equals(rule.Os.Name, context.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(rule.Os.Arch) && !ArchMatches(rule.Os.Arch, context.Arch))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(rule.Os.Version) && !VersionMatches(rule.Os.Version, context.OsVersion))
            {
                return false;
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (key, expected) in rule.Features)
            {
                var actual = context.Features.TryGetValue(key, out var value) && value;
                if (actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ArchMatches(string ruleArch, string contextArch)
    {
        if (string.Equals(ruleArch, contextArch, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Mojang иногда указывает "x86" для 32-бит, а мы возвращаем "x86_64".
        return ruleArch.Equals("x86", StringComparison.OrdinalIgnoreCase) && contextArch.Contains("x86", StringComparison.OrdinalIgnoreCase)
            && !contextArch.Contains("64", StringComparison.Ordinal);
    }

    public static bool VersionMatches(string pattern, string version)
    {
        var m = VersionPattern().Match(pattern.Trim());
        if (!m.Success)
        {
            // Нестандартный паттерн (например, регулярка) — не рискуем, считаем несовпадением.
            return false;
        }

        var op = m.Groups["op"].Value;
        if (!Version.TryParse(Normalize(m.Groups["ver"].Value), out var expected)
            || !Version.TryParse(Normalize(version.Split(' ')[0]), out var actual))
        {
            return false;
        }

        return op switch
        {
            "^" => actual.Major == expected.Major && actual >= expected,
            "~" => actual.Major == expected.Major && actual.Minor == expected.Minor && actual >= expected,
            ">=" => actual >= expected,
            "<=" => actual <= expected,
            ">" => actual > expected,
            "<" => actual < expected,
            _ => actual == expected
        };
    }

    private static string Normalize(string value)
    {
        var parts = value.Split('.');
        return parts.Length switch
        {
            1 => parts[0] + ".0.0",
            2 => parts[0] + "." + parts[1] + ".0",
            _ => string.Join('.', parts.Take(3))
        };
    }
}

/// <summary>Разбор Maven-координат библиотеки: group:artifact:version[:classifier].</summary>
public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier = null)
{
    public string ToPath(string extension = "jar")
        => $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{Artifact}-{Version}{(string.IsNullOrEmpty(Classifier) ? string.Empty : "-" + Classifier)}.{extension}";

    public string ToNotation()
        => string.IsNullOrEmpty(Classifier) ? $"{Group}:{Artifact}:{Version}" : $"{Group}:{Artifact}:{Version}:{Classifier}";

    public static MavenCoordinate Parse(string name)
    {
        var parts = name.Split(':');
        return parts.Length switch
        {
            // group:artifact:version[:classifier] — версия всегда третья, классификатор четвёртый.
            >= 4 => new MavenCoordinate(parts[0], parts[1], parts[2], parts[3]),
            3 => new MavenCoordinate(parts[0], parts[1], parts[2]),
            _ => throw new FormatException($"Некорректные Maven-координаты: '{name}'")
        };
    }
}
