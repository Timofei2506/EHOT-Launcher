using System.Text;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Game.Minecraft;

namespace EHOT.Launcher.Game.Launch;

/// <summary>Данные для подстановки плейсхолдеров аргументов (формат официального launcher).</summary>
public sealed record LaunchContext
{
    public required string MinecraftVersion { get; init; }
    public required string VersionType { get; init; }
    public required string GameDirectory { get; init; }
    public required string AssetsDirectory { get; init; }
    public required string AssetsIndex { get; init; }
    public required string LibraryDirectory { get; init; }
    public required string NativesDirectory { get; init; }
    public required string Classpath { get; init; }
    public required string MainClass { get; init; }
    public required string Username { get; init; }
    public required string Uuid { get; init; }
    public required string AccessToken { get; init; }
    public required string UserType { get; init; }
    public required string ClientId { get; init; }
    public required string XboxUid { get; init; }
    public required string GameAssetsDirectory { get; init; }
    public required int LauncherWidth { get; init; }
    public required int LauncherHeight { get; init; }
    public required string UserProperties { get; init; }
    public required string ProfileName { get; init; }
    public required string LauncherName { get; init; }
    public required string LauncherVersion { get; init; }
    public required int MemoryMb { get; init; }
    public string? LoggingArgument { get; init; }
}

public interface ILaunchArgumentBuilder
{
    IReadOnlyList<string> BuildJvmArguments(ResolvedVersion version, LaunchContext context);

    IReadOnlyList<string> BuildGameArguments(ResolvedVersion version, LaunchContext context);

    /// <summary>Собирает classpath из библиотек версии; отбрасывает несуществующие файлы с предупреждением.</summary>
    (string Classpath, IReadOnlyList<string> Missing) BuildClasspath(ResolvedVersion version, string librariesDirectory);
}

public sealed class LaunchArgumentBuilder(IPlatformInfo platform) : ILaunchArgumentBuilder
{
    public IReadOnlyList<string> BuildJvmArguments(ResolvedVersion version, LaunchContext context)
    {
        var args = new List<string>
        {
            $"-Xms{Math.Max(512, context.MemoryMb / 4)}M",
            $"-Xmx{context.MemoryMb}M",
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
            "-Dminecraft.launcher.brand=" + context.LauncherName,
            "-Dminecraft.launcher.version=" + context.LauncherVersion,
            "-Djava.library.path=" + context.NativesDirectory
        };

        if (platform.IsMacOS)
        {
            args.Add("-XstartOnFirstThread");
        }

        foreach (var entry in version.JvmArguments)
        {
            if (StartsWithNativePath(entry))
            {
                continue; // мы управляем natives через -Djava.library.path / --librarypath
            }

            args.Add(Substitute(entry, context));
        }

        if (!string.IsNullOrEmpty(context.LoggingArgument))
        {
            args.Add(Substitute(context.LoggingArgument, context));
        }

        // Обязательные системные свойства для корректной локали и macOS-меню.
        args.Add("-Duser.language=" + (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName));
        args.Add("-cp");
        args.Add(context.Classpath);
        args.Add(context.MainClass);
        return args;
    }

    public IReadOnlyList<string> BuildGameArguments(ResolvedVersion version, LaunchContext context)
    {
        var args = new List<string>();
        foreach (var entry in version.GameArguments)
        {
            args.Add(Substitute(entry, context));
        }

        return args;
    }

    public (string Classpath, IReadOnlyList<string> Missing) BuildClasspath(ResolvedVersion version, string librariesDirectory)
    {
        var separator = platform.IsWindows ? ';' : ':';
        var paths = new List<string>();
        var missing = new List<string>();

        foreach (var library in version.Libraries)
        {
            var artifact = library.Downloads?.Artifact;
            if (artifact?.Path is not null)
            {
                var full = Path.Combine(librariesDirectory, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    paths.Add(full);
                }
                else
                {
                    missing.Add(full);
                }

                continue;
            }

            // Локальные библиотеки без downloads (их так описывают OptiFine и часть лоадеров)
            // тоже должны попасть в classpath — иначе версия ставится, но не запускается.
            if (library.Name.Contains(':') && library.Natives is null)
            {
                var coordinate = MavenCoordinate.Parse(library.Name);
                var full = Path.Combine(librariesDirectory, coordinate.ToPath().Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    paths.Add(full);
                }
                else
                {
                    missing.Add(full);
                }
            }
        }

        // Клиентский JAR: у модифицированной версии (OptiFine, лоадер) он свой, пропатченный,
        // и только если его нет — берём ванильный из базовой версии.
        var ownJar = Path.Combine(librariesDirectory, "..", "versions", version.Id, $"{version.Id}.jar");
        var ownFull = Path.GetFullPath(ownJar);
        var clientJar = Path.Combine(librariesDirectory, "..", "versions", version.BaseVersionId, $"{version.BaseVersionId}.jar");
        var clientFull = Path.GetFullPath(clientJar);
        if (File.Exists(ownFull))
        {
            paths.Add(ownFull);
        }
        else if (File.Exists(clientFull))
        {
            paths.Add(clientFull);
        }
        else
        {
            missing.Add(clientFull);
        }

        return (string.Join(separator, paths.Distinct()), missing);
    }

    private static bool StartsWithNativePath(string arg)
        => arg.StartsWith("-Djava.library.path", StringComparison.OrdinalIgnoreCase)
           || arg.StartsWith("-Dorg.lwjgl.librarypath", StringComparison.OrdinalIgnoreCase)
           || arg.StartsWith("-Dminecraft.launcher", StringComparison.OrdinalIgnoreCase);

    public string Substitute(string template, LaunchContext c)
    {
        if (!template.Contains("${", StringComparison.Ordinal))
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, start - i);
            var end = template.IndexOf('}', start);
            if (end < 0)
            {
                sb.Append(template, start, template.Length - start);
                break;
            }

            var key = template[(start + 2)..end];
            sb.Append(Resolve(key, c));
            i = end + 1;
        }

        return sb.ToString();
    }

    private string Resolve(string key, LaunchContext c) => key switch
    {
        "auth_player_name" => c.Username,
        "version_name" => c.MinecraftVersion,
        "game_directory" => c.GameDirectory,
        "assets_root" => c.AssetsDirectory,
        "assets_index_name" => c.AssetsIndex,
        "auth_uuid" => c.Uuid,
        "auth_access_token" => c.AccessToken,
        "auth_session" => c.AccessToken, // legacy
        "user_type" => c.UserType,
        "version_type" => c.VersionType,
        "natives_directory" => c.NativesDirectory,
        "launcher_name" => c.LauncherName,
        "launcher_version" => c.LauncherVersion,
        "classpath" => c.Classpath,
        "classpath_separator" => platform.IsWindows ? ";" : ":",
        "library_directory" => c.LibraryDirectory,
        "launcher_profiles" => c.ProfileName,
        "auth_xuid" => c.XboxUid,
        "clientid" => c.ClientId,
        "resolution_width" => c.LauncherWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "resolution_height" => c.LauncherHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "user_properties" => c.UserProperties,
        "game_assets" => c.GameAssetsDirectory,
        "quickPlayPath" => string.Empty,
        "quickPlaySingleplayer" => string.Empty,
        "quickPlayMultiplayer" => string.Empty,
        "quickPlayRealms" => string.Empty,
        _ => string.Empty
    };
}
