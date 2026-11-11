using System.Text.Json;
using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;

namespace EHOT.Launcher.Content.Modrinth;

/// <summary>
/// Modrinth API v2. Официальные лимиты — 300 запросов/минуту;
/// мы держим собственный троттлинг и кэш ответов, чтобы не злоупотреблять сервисом.
/// </summary>
public sealed class ModrinthProvider(IHttpService http, ILogService log) : IContentProvider
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    private static readonly Dictionary<ContentSort, string> SortIndex = new()
    {
        [ContentSort.Relevance] = "relevance",
        [ContentSort.Downloads] = "downloads",
        [ContentSort.Follows] = "follows",
        [ContentSort.Newest] = "newest",
        [ContentSort.Updated] = "updated"
    };

    public string Id => "modrinth";

    public string DisplayName => "Modrinth";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public ContentType[] SupportedTypes { get; } =
        [ContentType.Mod, ContentType.Modpack, ContentType.ResourcePack, ContentType.Shader, ContentType.World];

    public async Task<ContentSearchPage> SearchAsync(ContentSearchQuery query, CancellationToken ct = default)
    {
        var facets = new List<string> { $"[\"project_type:{TypeToFacet(query.Type)}\"]" };
        if (!string.IsNullOrWhiteSpace(query.MinecraftVersion))
        {
            facets.Add($"[\"versions:{query.MinecraftVersion}\"]");
        }

        if (query.Loader is { } loader && loader != LoaderKind.Vanilla && IsLoaderApplicable(query.Type))
        {
            facets.Add($"[\"categories:{LoaderToCategory(loader)}\"]");
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            facets.Add($"[\"categories:{query.Category}\"]");
        }

        var url = $"{BaseUrl}/search?limit={Math.Clamp(query.Limit, 1, 100)}&offset={Math.Max(0, query.Offset)}" +
                  $"&index={SortIndex.GetValueOrDefault(query.Sort, "relevance")}" +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            url += $"&query={Uri.EscapeDataString(query.Text)}";
        }

        var response = await http.GetJsonAsync<ModrinthSearchResponse>(new Uri(url), ct: ct).ConfigureAwait(false);
        if (response is null)
        {
            log.Warn("Modrinth", "Поиск не удался (пустой ответ).");
            return new ContentSearchPage([], 0, query.Offset, query.Limit);
        }

        var items = response.Hits.Select(h => MapHit(h, query.Type)).ToArray();
        return new ContentSearchPage(items, response.TotalHits, query.Offset, query.Limit);
    }

    public async Task<ContentProject?> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
    {
        var project = await http.GetJsonAsync<ModrinthProject>(
            new Uri($"{BaseUrl}/project/{Uri.EscapeDataString(idOrSlug)}"), ct: ct).ConfigureAwait(false);

        if (project is null)
        {
            return null;
        }

        var author = await TryGetAuthorAsync(project.Id, ct).ConfigureAwait(false);

        return new ContentProject
        {
            ProviderId = Id,
            Id = project.Id,
            Slug = project.Slug,
            Title = project.Title,
            Description = project.Description,
            Type = MapType(project.ProjectType),
            Author = author,
            IconUrl = project.IconUrl,
            Downloads = project.Downloads,
            Follows = project.Follows,
            Categories = project.Categories,
            MinecraftVersions = project.GameVersions,
            Loaders = project.Loaders,
            License = project.License?.Name ?? project.License?.Id,
            UpdatedAt = project.DateModified,
            BodyHtml = project.Body,
            Gallery = project.Gallery?.Select(g => g.Url).ToArray() ?? []
        };
    }

    public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
        string projectId, string? minecraftVersion = null, LoaderKind? loader = null, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        var query = new List<string>();

        if (!string.IsNullOrWhiteSpace(minecraftVersion))
        {
            query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion })));
        }

        if (loader is { } l && l != LoaderKind.Vanilla)
        {
            query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { LoaderToCategory(l) })));
        }

        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        var versions = await http.GetJsonAsync<List<ModrinthVersion>>(new Uri(url), ct: ct).ConfigureAwait(false);
        if (versions is null)
        {
            return [];
        }

        var project = await GetProjectAsync(projectId, ct).ConfigureAwait(false);

        return versions.Select(v => MapVersion(v, project)).ToArray();
    }

    public async Task<ContentVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        var version = await http.GetJsonAsync<ModrinthVersion>(
            new Uri($"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}"), ct: ct).ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var project = await GetProjectAsync(version.ProjectId, ct).ConfigureAwait(false);
        return MapVersion(version, project);
    }

    private async Task<string?> TryGetAuthorAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var members = await http.GetJsonAsync<List<ModrinthMember>>(
                new Uri($"{BaseUrl}/project/{projectId}/members"), ct: ct).ConfigureAwait(false);
            return members?.FirstOrDefault()?.User?.Username;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ContentProject MapHit(ModrinthHit hit, ContentType requestedType) => new()
    {
        ProviderId = "modrinth",
        Id = hit.ProjectId,
        Slug = hit.Slug,
        Title = hit.Title,
        Description = hit.Description,
        Type = requestedType,
        Author = hit.Author,
        IconUrl = hit.IconUrl,
        Downloads = hit.Downloads,
        Follows = hit.Follows,
        Categories = hit.DisplayCategories ?? hit.Categories,
        MinecraftVersions = hit.Versions,
        UpdatedAt = hit.DateModified,
        Gallery = hit.Gallery ?? []
    };

    private static ContentVersion MapVersion(ModrinthVersion v, ContentProject? project) => new()
    {
        ProviderId = "modrinth",
        Id = v.Id,
        ProjectId = v.ProjectId,
        ProjectTitle = project?.Title ?? string.Empty,
        Name = v.Name,
        VersionNumber = v.VersionNumber,
        GameVersions = v.GameVersions,
        Loaders = v.Loaders,
        Dependencies = v.Dependencies.Select(d => new ContentDependencyRef
        {
            VersionId = d.VersionId,
            ProjectId = d.ProjectId,
            FileName = d.FileName,
            Kind = d.DependencyType
        }).ToArray(),
        Files = v.Files.Select(f => new ContentFile
        {
            Url = f.Url,
            FileName = f.Filename,
            Size = f.Size,
            Primary = f.Primary,
            Sha1 = f.Hashes.GetValueOrDefault("sha1"),
            Sha512 = f.Hashes.GetValueOrDefault("sha512")
        }).ToArray(),
        PublishedAt = v.DatePublished,
        IconUrl = project?.IconUrl,
        Type = project?.Type ?? ContentType.Mod
    };

    private static string TypeToFacet(ContentType type) => type switch
    {
        ContentType.Mod => "mod",
        ContentType.Modpack => "modpack",
        ContentType.ResourcePack => "resourcepack",
        ContentType.Shader => "shader",
        // В Modrinth нет отдельного типа «мир»: карты распространяются как datapack/project.
        ContentType.World => "datapack",
        ContentType.Plugin => "plugin",
        _ => "mod"
    };

    private static bool IsLoaderApplicable(ContentType type)
        => type is ContentType.Mod or ContentType.Modpack or ContentType.Plugin;

    public static string LoaderToCategory(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        LoaderKind.Quilt => "quilt",
        _ => "vanilla"
    };

    private static ContentType MapType(string projectType) => projectType switch
    {
        "mod" => ContentType.Mod,
        "modpack" => ContentType.Modpack,
        "resourcepack" => ContentType.ResourcePack,
        "shader" => ContentType.Shader,
        "datapack" => ContentType.World,
        "plugin" => ContentType.Plugin,
        _ => ContentType.Mod
    };

    private sealed class ModrinthMember
    {
        [System.Text.Json.Serialization.JsonPropertyName("user")]
        public ModrinthUser? User { get; set; }
    }

    private sealed class ModrinthUser
    {
        [System.Text.Json.Serialization.JsonPropertyName("username")]
        public string? Username { get; set; }
    }
}
