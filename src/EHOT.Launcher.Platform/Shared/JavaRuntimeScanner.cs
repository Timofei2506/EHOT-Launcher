using System.Globalization;
using System.Text.RegularExpressions;
using EHOT.Launcher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Platform.Shared;

public sealed record JavaRuntime(string Path, string Version, int MajorVersion, string Vendor, string Architecture, bool IsJdk)
{
    public string DisplayName => $"Java {Version} · {Vendor} · {(IsJdk ? "JDK" : "JRE")}";

    public bool Matches(int requiredMajor) => MajorVersion >= requiredMajor;
}

/// <summary>
/// Поиск Java: переменные окружения, PATH, стандартные каталоги платформы, каталог лаунчера.
/// Версия читается из файла release в корне JRE (это быстрее и надёжнее вызова java -version),
/// с fallback на запуск `java -version`.
/// </summary>
public interface IJavaRuntimeService
{
    Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(CancellationToken ct = default);

    /// <summary>Выбирает лучший рантайм под требуемую мажорную версию.</summary>
    JavaRuntime? SelectBest(IReadOnlyList<JavaRuntime> runtimes, int requiredMajor);
}

public sealed partial class JavaRuntimeService(
    IProcessRunner processRunner,
    IAppPaths paths,
    IPlatformInfo platformInfo,
    ILogger<JavaRuntimeService> logger) : IJavaRuntimeService
{
    [GeneratedRegex(@"version\s+""(?<v>[^""]+)""")]
    private static partial Regex ReleaseVersionRegex();

    [GeneratedRegex(@"(?:java|openjdk)\s+version\s+""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionOutputRegex();

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(CancellationToken ct = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Каталог рантаймов, управляемых лаунчером.
        var managed = System.IO.Path.Combine(paths.AppDataDirectory, "runtimes");
        if (Directory.Exists(managed))
        {
            foreach (var dir in Directory.EnumerateDirectories(managed, "*", SearchOption.TopDirectoryOnly))
            {
                AddCandidate(candidates, dir);
            }
        }

        // 2. Переменные окружения.
        foreach (var env in new[] { "JAVA_HOME", "JDK_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(value))
            {
                AddCandidate(candidates, value);
            }
        }

        // 3. PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = System.IO.Path.Combine(dir, OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(exe))
            {
                AddCandidate(candidates, dir);
            }
        }

        // 4. Стандартные места ОС.
        foreach (var root in PlatformJavaRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    AddCandidate(candidates, dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Пропуск каталога Java {Root}: {Message}", root, ex.Message);
            }
        }

        // 5. macOS: /usr/libexec/java_home может знать больше, чем перебор каталогов.
        if (platformInfo.IsMacOS && File.Exists("/usr/libexec/java_home"))
        {
            try
            {
                var result = await processRunner.RunAsync(
                    new ProcessRequest("/usr/libexec/java_home", ["-V"]), ct).ConfigureAwait(false);
                foreach (var line in (result.StandardError + result.StandardOutput).Split('\n'))
                {
                    var idx = line.IndexOf('/');
                    if (idx > 0 && line.Contains("jdk", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = line[..].Trim().TrimEnd('/');
                        var slash = path.IndexOf('/');
                        if (slash == 0)
                        {
                            AddCandidate(candidates, path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("java_home недоступен: {Message}", ex.Message);
            }
        }

        var results = new List<JavaRuntime>();
        foreach (var home in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var runtime = await ProbeAsync(home, ct).ConfigureAwait(false);
            if (runtime is not null)
            {
                results.Add(runtime);
            }
        }

        logger.LogInformation("Найдено Java-рантаймов: {Count}", results.Count);
        return results
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.MajorVersion)
            .ThenByDescending(r => r.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public JavaRuntime? SelectBest(IReadOnlyList<JavaRuntime> runtimes, int requiredMajor)
    {
        if (runtimes.Count == 0)
        {
            return null;
        }

        // Сначала — точное совпадение мажорной версии, затем ближайшая старшая, затем младшая.
        var exact = runtimes.Where(r => r.MajorVersion == requiredMajor).OrderByDescending(r => r.IsJdk).FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        var newer = runtimes.Where(r => r.MajorVersion > requiredMajor).OrderBy(r => r.MajorVersion).FirstOrDefault();
        return newer ?? runtimes.OrderByDescending(r => r.MajorVersion).First();
    }

    private async Task<JavaRuntime?> ProbeAsync(string home, CancellationToken ct)
    {
        try
        {
            var exe = JavaExecutableIn(home);
            if (exe is null)
            {
                return null;
            }

            var releaseFile = System.IO.Path.Combine(home, "release");
            string? version = null;
            string vendor = "Unknown";
            bool isJdk = File.Exists(System.IO.Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"));

            if (File.Exists(releaseFile))
            {
                var content = await File.ReadAllTextAsync(releaseFile, ct).ConfigureAwait(false);
                var m = ReleaseVersionRegex().Match(content);
                if (m.Success)
                {
                    version = m.Groups["v"].Value;
                }

                foreach (var line in content.Split('\n'))
                {
                    if (line.StartsWith("IMPLEMENTOR=", StringComparison.Ordinal))
                    {
                        vendor = line["IMPLEMENTOR=".Length..].Trim().Trim('"');
                    }
                }
            }

            if (version is null)
            {
                var result = await processRunner.RunAsync(new ProcessRequest(exe, ["-version"]), ct).ConfigureAwait(false);
                var m = JavaVersionOutputRegex().Match(result.StandardError + result.StandardOutput);
                if (!m.Success)
                {
                    return null;
                }

                version = m.Groups["v"].Value;
            }

            var major = ParseMajor(version);
            if (major <= 0)
            {
                return null;
            }

            var arch = platformInfo.Architecture;
            return new JavaRuntime(home, version, major, vendor, arch, isJdk);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Не удалось определить Java в {Home}: {Message}", home, ex.Message);
            return null;
        }
    }

    private static string? JavaExecutableIn(string home)
    {
        var exe = System.IO.Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(exe))
        {
            return exe;
        }

        // macOS: Home/Contents/Home/bin/java
        var nested = System.IO.Path.Combine(home, "Contents", "Home", "bin", "java");
        return File.Exists(nested) ? nested : null;
    }

    private static void AddCandidate(HashSet<string> set, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = path.TrimEnd('"', ' ');
        if (!Directory.Exists(normalized))
        {
            return;
        }

        if (File.Exists(System.IO.Path.Combine(normalized, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"))
            || File.Exists(System.IO.Path.Combine(normalized, "Contents", "Home", "bin", "java")))
        {
            set.Add(normalized);
        }
    }

    private IEnumerable<string> PlatformJavaRoots()
    {
        if (platformInfo.IsWindows)
        {
            yield return @"C:\Program Files\Java";
            yield return @"C:\Program Files\Eclipse Adoptium";
            yield return @"C:\Program Files\Microsoft";
            yield return @"C:\Program Files\Zulu";
            yield return @"C:\Program Files\Amazon Corretto";

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var program in new[] { "Programs\\Eclipse Adoptium", "Packages" })
            {
                var p = System.IO.Path.Combine(localAppData, program);
                if (Directory.Exists(p))
                {
                    yield return p;
                }
            }
        }
        else if (platformInfo.IsMacOS)
        {
            yield return "/Library/Java/JavaVirtualMachines";
            yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Java", "JavaVirtualMachines");
            yield return "/opt/homebrew/opt/openjdk/libexec";
            yield return "/opt/homebrew/Cellar";
            yield return "/usr/local/opt/openjdk/libexec";
        }
        else
        {
            yield return "/usr/lib/jvm";
            yield return "/usr/java";
            yield return "/opt/java";
        }
    }

    /// <summary>Разбор версии Java: "1.8.0_392" → 8, "17.0.9" → 17, "21" → 21.</summary>
    public static int ParseMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return 0;
        }

        var cleaned = version.Trim().TrimStart('"');
        var parts = cleaned.Split('.', '_', '-', '+');
        if (parts.Length == 0)
        {
            return 0;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first))
        {
            return 0;
        }

        if (first == 1 && parts.Length > 1
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacy))
        {
            return legacy;
        }

        return first;
    }
}
