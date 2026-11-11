using System.Text.Json.Serialization;

namespace EHOT.Launcher.Content.Modrinth;

public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")]
    public List<ModrinthHit> Hits { get; set; } = [];

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }
}

public sealed class ModrinthHit
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("display_categories")]
    public List<string>? DisplayCategories { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("follows")]
    public long Follows { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset? DateModified { get; set; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }

    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = [];

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("gallery")]
    public List<string>? Gallery { get; set; }

    [JsonPropertyName("client_side")]
    public string? ClientSide { get; set; }

    [JsonPropertyName("server_side")]
    public string? ServerSide { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("follows")]
    public long Follows { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("gallery")]
    public List<ModrinthGalleryImage>? Gallery { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("license")]
    public ModrinthLicense? License { get; set; }

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("date_published")]
    public DateTimeOffset? DatePublished { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset? DateModified { get; set; }

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }
}

public sealed class ModrinthGalleryImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("featured")]
    public bool Featured { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public sealed class ModrinthLicense
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = string.Empty;

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = string.Empty;

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency> Dependencies { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];

    [JsonPropertyName("date_published")]
    public DateTimeOffset? DatePublished { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; set; } = "required";
}

public sealed class ModrinthFile
{
    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; set; } = [];

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("file_type")]
    public string? FileType { get; set; }
}
