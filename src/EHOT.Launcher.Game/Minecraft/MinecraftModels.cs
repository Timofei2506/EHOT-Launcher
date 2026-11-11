using System.Text.Json.Serialization;

namespace EHOT.Launcher.Game.Minecraft;

/// <summary>DTO официального манифеста версий Mojang.</summary>
public sealed class VersionManifestDto
{
    [JsonPropertyName("latest")]
    public LatestVersions? Latest { get; set; }

    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset ReleaseTime { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }
}

/// <summary>Полный version.json.</summary>
public sealed class VersionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int? MinimumLauncherVersion { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("downloads")]
    public Dictionary<string, DownloadInfo>? Downloads { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionInfo? JavaVersion { get; set; }

    [JsonPropertyName("libraries")]
    public List<LibraryDto> Libraries { get; set; } = [];

    [JsonPropertyName("arguments")]
    public ArgumentsDto? Arguments { get; set; }

    [JsonPropertyName("minecraftArguments")]
    public string? LegacyMinecraftArguments { get; set; }

    [JsonPropertyName("logging")]
    public LoggingDto? Logging { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset? ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public sealed class DownloadInfo
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class JavaVersionInfo
{
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

public sealed class LibraryDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("extract")]
    public ExtractRules? Extract { get; set; }

    [JsonPropertyName("rules")]
    public List<RuleDto>? Rules { get; set; }

    [JsonPropertyName("url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadInfo? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class ExtractRules
{
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }
}

public sealed class ArgumentsDto
{
    [JsonPropertyName("game")]
    public List<ArgumentEntry> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    public List<ArgumentEntry> Jvm { get; set; } = [];
}

/// <summary>Элемент arguments: строка или объект с rules.</summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public string? Value { get; init; }

    public List<string> Values { get; init; } = [];

    public List<RuleDto> Rules { get; init; } = [];

    public IEnumerable<string> Expand() => Value is not null ? [Value] : Values;
}

public sealed class RuleDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }
}

public sealed class LoggingDto
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    [JsonPropertyName("file")]
    public LoggingFile? File { get; set; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Запись в индексе ассетов.</summary>
public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class AssetIndexDto
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = [];

    [JsonPropertyName("map_to_resources")]
    public bool? MapToResources { get; set; }

    [JsonPropertyName("virtual")]
    public bool? Virtual { get; set; }
}

/// <summary>Ответ meta.fabricmc.net / meta.quiltmc.org для профиля лоадера.</summary>
public sealed class LoaderProfileDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("libraries")]
    public List<LibraryDto> Libraries { get; set; } = [];

    [JsonPropertyName("arguments")]
    public ArgumentsDto? Arguments { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
