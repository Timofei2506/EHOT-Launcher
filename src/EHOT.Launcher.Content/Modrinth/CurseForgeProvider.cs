using System.Text.Json.Serialization;
using EHOT.Launcher.Content.Models;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;

namespace EHOT.Launcher.Content.Modrinth;

/// <summary>
/// CurseForge через официальный API (api.curseforge.com).
/// Требует API-ключ, выданный CurseForge; ключ лаунчер не содержит и не может содержать.
/// Без ключа провайдер честно сообщает, что недоступен, вместо пустого/фейкового каталога.
/// </summary>
public sealed class CurseForgeProvider(IHttpService http, ILogService log, string? apiKey) : IContentProvider
{
    private const string BaseUrl = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;
    private const int ModsClassId = 6;
    private const int ModpacksClassId = 4471;
    private const int ResourcePacksClassId = 12;
    private const int WorldsClassId = 17;
    private const int ShadersClassId = 6552;

    public string Id => "curseforge";

    public string DisplayName => "CurseForge";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey);

    public string? UnavailableReason => IsAvailable
        ? null
        : "Для CurseForge нужен собственный API-ключ (Eternal API key). Укажите его в Настройках → Каталог, " +
          "если у вас есть доступ. Modrinth работает без ключа.";

    public ContentType[] SupportedTypes { get; } =
        [ContentType.Mod, ContentType.Modpack, ContentType.ResourcePack, ContentType.World, ContentType.Shader];

    public async Task<ContentSearchPage> SearchAsync(ContentSearchQuery query, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return new ContentSearchPage([], 0, query.Offset, query.Limit);
        }

        var body = new Dictionary<string, object?>
        {
            ["gameId"] = MinecraftGameId,
            ["classId"] = TypeToClassId(query.Type),
            ["index"] = query.Offset,
            ["pageSize"] = Math.Clamp(query.Limit, 1, 50),
            ["sortField"] = query.Sort switch
            {
                ContentSort.Downloads => 6,
                ContentSort.Follows => 2,
                ContentSort.Newest => 11,
                ContentSort.Updated => 3,
                _ => 1
            },
            ["sortOrder"] = "desc"
        };

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            body["searchFilter"] = query.Text;
        }

        if (!string.IsNullOrWhiteSpace(query.MinecraftVersion))
        {
            body["gameVersion"] = query.MinecraftVersion;
        }

        var response = await SendAsync<CurseForgeResponse<List<CurseForgeMod>>>(HttpMethod.Post, "/mods/search", body, ct).ConfigureAwait(false);
        if (response?.Data is null)
        {
            return new ContentSearchPage([], 0, query.Offset, query.Limit);
        }

        var items = response.Data.Select(m => MapMod(m, query.Type)).ToArray();
        return new ContentSearchPage(items, response.Pagination?.TotalCount ?? items.Length, query.Offset, query.Limit);
    }

    public async Task<ContentProject?> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        if (int.TryParse(idOrSlug, out var id))
        {
            var response = await SendAsync<CurseForgeResponse<CurseForgeMod>>(HttpMethod.Get, $"/mods/{id}", null, ct).ConfigureAwait(false);
            return response?.Data is null ? null : MapMod(response.Data, ContentType.Mod);
        }

        // CurseForge не ищет по slug напрямую — идём через поиск.
        var search = await SearchAsync(new ContentSearchQuery { Text = idOrSlug, Limit = 5 }, ct).ConfigureAwait(false);
        var items = search.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Slug, idOrSlug, StringComparison.OrdinalIgnoreCase))
            {
                return items[i];
            }
        }

        return items.Count > 0 ? items[0] : null;
    }

    public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
        string projectId, string? minecraftVersion = null, LoaderKind? loader = null, CancellationToken ct = default)
    {
        if (!IsAvailable || !int.TryParse(projectId, out var id))
        {
            return [];
        }

        var url = $"/mods/{id}/files?pageSize=50";
        if (!string.IsNullOrWhiteSpace(minecraftVersion))
        {
            url += $"&gameVersion={Uri.EscapeDataString(minecraftVersion)}";
        }

        var response = await SendAsync<CurseForgeResponse<List<CurseForgeFile>>>(
            HttpMethod.Get, url, null, ct).ConfigureAwait(false);

        if (response?.Data is null)
        {
            return [];
        }

        return response.Data
            .Where(f => f.ReleaseType is 1 or 2) // release / beta
            .Select(f => MapFile(f, projectId, loader))
            .ToArray();
    }

    public async Task<ContentVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        if (!IsAvailable || !int.TryParse(versionId, out var id))
        {
            return null;
        }

        var response = await SendAsync<CurseForgeResponse<CurseForgeFile>>(HttpMethod.Get, $"/mods/files/{id}", null, ct).ConfigureAwait(false);
        return response?.Data is null ? null : MapFile(response.Data, response.Data.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string relative, object? body, CancellationToken ct)
        where T : class
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            // CurseForge авторизуется заголовком x-api-key; шлём его через общий HTTP-слой,
            // чтобы работали общий пул соединений, таймауты, повторы и белый список хостов.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-api-key"] = apiKey!
            };

            var uri = new Uri(BaseUrl + relative);
            if (method == HttpMethod.Get)
            {
                return await http.GetJsonAsync<T>(uri, ct: ct, headers: headers).ConfigureAwait(false);
            }

            return await http.SendJsonAsync<T>(method, uri, body, ct: ct, headers: headers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.Warn("CurseForge", $"Ошибка запроса {relative}: {ex.Message}");
            return null;
        }
    }

    private static ContentProject MapMod(CurseForgeMod mod, ContentType type) => new()
    {
        ProviderId = "curseforge",
        Id = mod.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Slug = mod.Slug,
        Title = mod.Name,
        Description = mod.Summary,
        Type = type,
        Author = mod.Authors?.FirstOrDefault()?.Name,
        IconUrl = mod.Logo?.ThumbnailUrl,
        Downloads = mod.DownloadCount,
        Categories = mod.Categories?.Select(c => c.Name).ToArray() ?? [],
        Loaders = [],
        UpdatedAt = mod.DateModified
    };

    private static ContentVersion MapFile(CurseForgeFile file, string projectId, LoaderKind? loader) => new()
    {
        ProviderId = "curseforge",
        Id = file.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ProjectId = projectId,
        ProjectTitle = string.Empty,
        Name = file.DisplayName,
        VersionNumber = file.DisplayName,
        GameVersions = file.GameVersions,
        Loaders = loader is null ? [] : [ModrinthProvider.LoaderToCategory(loader.Value)],
        Dependencies = file.Dependencies?.Select(d => new ContentDependencyRef
        {
            ProjectId = d.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind = d.RelationType == 3 ? "required" : "optional"
        }).ToArray() ?? [],
        Files =
        [
            new ContentFile
            {
                Url = file.DownloadUrl ?? string.Empty,
                FileName = file.FileName,
                Size = file.FileLength,
                Primary = true,
                Sha1 = file.Hashes?.GetValueOrDefault("sha1")
            }
        ],
        PublishedAt = file.FileDate,
        Type = ContentType.Mod
    };

    private static int TypeToClassId(ContentType type) => type switch
    {
        ContentType.Modpack => ModpacksClassId,
        ContentType.ResourcePack => ResourcePacksClassId,
        ContentType.World => WorldsClassId,
        ContentType.Shader => ShadersClassId,
        _ => ModsClassId
    };

    private sealed class CurseForgeResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("pagination")]
        public CurseForgePagination? Pagination { get; set; }
    }

    private sealed class CurseForgePagination
    {
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }
    }

    private sealed class CurseForgeMod
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("downloadCount")]
        public long DownloadCount { get; set; }

        [JsonPropertyName("logo")]
        public CurseForgeLogo? Logo { get; set; }

        [JsonPropertyName("authors")]
        public List<CurseForgeAuthor>? Authors { get; set; }

        [JsonPropertyName("categories")]
        public List<CurseForgeCategory>? Categories { get; set; }

        [JsonPropertyName("dateModified")]
        public DateTimeOffset? DateModified { get; set; }
    }

    private sealed class CurseForgeLogo
    {
        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; set; }
    }

    private sealed class CurseForgeAuthor
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CurseForgeCategory
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CurseForgeFile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("fileDate")]
        public DateTimeOffset? FileDate { get; set; }

        [JsonPropertyName("fileLength")]
        public long FileLength { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("gameVersions")]
        public List<string> GameVersions { get; set; } = [];

        [JsonPropertyName("releaseType")]
        public int ReleaseType { get; set; }

        [JsonPropertyName("dependencies")]
        public List<CurseForgeDependency>? Dependencies { get; set; }

        [JsonPropertyName("hashes")]
        public Dictionary<string, string>? Hashes { get; set; }
    }

    private sealed class CurseForgeDependency
    {
        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("relationType")]
        public int RelationType { get; set; }
    }
}
