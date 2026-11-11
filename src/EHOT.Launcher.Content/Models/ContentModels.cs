using EHOT.Launcher.Core.Models;

namespace EHOT.Launcher.Content.Models;

public sealed record ContentSearchQuery
{
    public string? Text { get; init; }
    public ContentType Type { get; init; } = ContentType.Mod;
    public string? MinecraftVersion { get; init; }
    public LoaderKind? Loader { get; init; }
    public string? Category { get; init; }
    public ContentSort Sort { get; init; } = ContentSort.Relevance;
    public int Offset { get; init; }
    public int Limit { get; init; } = 20;
}

public enum ContentSort
{
    Relevance = 0,
    Downloads = 1,
    Follows = 2,
    Newest = 3,
    Updated = 4
}

public sealed record ContentProject
{
    public required string ProviderId { get; init; }
    public required string Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required ContentType Type { get; init; }
    public string? Author { get; init; }
    public string? IconUrl { get; init; }
    public long Downloads { get; init; }
    public long Follows { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> MinecraftVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public string? License { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? BodyHtml { get; init; }
    public IReadOnlyList<string> Gallery { get; init; } = [];
}

public sealed record ContentSearchPage(IReadOnlyList<ContentProject> Items, int TotalHits, int Offset, int Limit)
{
    public bool HasMore => Offset + Items.Count < TotalHits;
}

public sealed record ContentDependencyRef
{
    public string? VersionId { get; init; }
    public string? ProjectId { get; init; }
    public string? FileName { get; init; }

    /// <summary>required | optional | incompatible | embedded.</summary>
    public required string Kind { get; init; }
}

public sealed record ContentVersion
{
    public required string ProviderId { get; init; }
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectTitle { get; init; }
    public required string Name { get; init; }
    public required string VersionNumber { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<ContentDependencyRef> Dependencies { get; init; } = [];
    public IReadOnlyList<ContentFile> Files { get; init; } = [];
    public DateTimeOffset? PublishedAt { get; init; }
    public string? IconUrl { get; init; }
    public ContentType Type { get; init; }

    public ContentFile? PrimaryFile
    {
        get
        {
            if (Files.Count == 0)
            {
                return null;
            }

            for (var i = 0; i < Files.Count; i++)
            {
                if (Files[i].Primary)
                {
                    return Files[i];
                }
            }

            return Files[0];
        }
    }
}

public sealed record ContentFile
{
    public required string Url { get; init; }
    public required string FileName { get; init; }
    public long Size { get; init; }
    public bool Primary { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha512 { get; init; }
}

/// <summary>Провайдер каталога. Реализации: Modrinth (без ключа), CurseForge (нужен API-ключ).</summary>
public interface IContentProvider
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Доступен ли провайдер в текущей конфигурации (например, есть ли ключ CurseForge).</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    ContentType[] SupportedTypes { get; }

    Task<ContentSearchPage> SearchAsync(ContentSearchQuery query, CancellationToken ct = default);

    Task<ContentProject?> GetProjectAsync(string idOrSlug, CancellationToken ct = default);

    Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(string projectId, string? minecraftVersion = null, LoaderKind? loader = null, CancellationToken ct = default);

    Task<ContentVersion?> GetVersionAsync(string versionId, CancellationToken ct = default);
}

public interface IContentProviderRegistry
{
    IReadOnlyList<IContentProvider> Providers { get; }

    IContentProvider? Get(string id);

    IContentProvider Primary { get; }
}
