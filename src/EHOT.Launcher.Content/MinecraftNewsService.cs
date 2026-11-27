using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Net;

namespace EHOT.Launcher.Content;

/// <summary>Новости Java Edition из официального launchercontent.mojang.com.</summary>
public interface IMinecraftNewsService
{
    Task<IReadOnlyList<NewsItem>> GetLatestAsync(int count = 8, CancellationToken ct = default);

    Task<string?> GetArticleHtmlAsync(string contentPath, CancellationToken ct = default);

    Task<NewsArticle?> GetArticleAsync(string contentPath, CancellationToken ct = default);
}

/// <summary>Полная официальная статья Java Edition (заголовок, обложка, HTML-текст).</summary>
public sealed record NewsArticle(string Title, string Version, string Type, string ImageUrl, string BodyHtml);

public sealed class MinecraftNewsService(IHttpService http, ILogService log) : IMinecraftNewsService
{
    private const string IndexUrl = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
    private const string ImageRoot = "https://launchercontent.mojang.com";

    private IReadOnlyList<NewsItem>? _cache;
    private DateTimeOffset _cachedAt;

    public async Task<IReadOnlyList<NewsItem>> GetLatestAsync(int count = 8, CancellationToken ct = default)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(30))
        {
            return _cache.Take(count).ToArray();
        }

        try
        {
            var response = await http.GetJsonAsync<LauncherContentResponse>(new Uri(IndexUrl), ct: ct).ConfigureAwait(false);
            if (response?.Entries is null)
            {
                return _cache ?? [];
            }

            var items = response.Entries
                .Where(e => e.Title is not null && e.ContentPath is not null)
                .OrderByDescending(e => e.Date)
                .Select(e => new NewsItem(
                    e.Title!,
                    e.Version ?? string.Empty,
                    e.Type ?? "release",
                    e.Image?.Url is null ? string.Empty : ImageRoot + e.Image.Url,
                    e.Date,
                    e.ContentPath!,
                    e.Id ?? string.Empty))
                .ToArray();

            _cache = items;
            _cachedAt = DateTimeOffset.UtcNow;
            log.Info("News", $"Получено новостей: {items.Length}");
            return items.Take(count).ToArray();
        }
        catch (Exception ex)
        {
            log.Warn("News", "Не удалось загрузить новости", ex);
            return _cache ?? [];
        }
    }

    public async Task<string?> GetArticleHtmlAsync(string contentPath, CancellationToken ct = default)
    {
        var article = await GetArticleAsync(contentPath, ct).ConfigureAwait(false);
        return article?.BodyHtml;
    }

    public async Task<NewsArticle?> GetArticleAsync(string contentPath, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://launchercontent.mojang.com/v2/{contentPath.TrimStart('/')}";
            var article = await http.GetJsonAsync<LauncherContentArticle>(new Uri(url), ct: ct).ConfigureAwait(false);
            if (article?.Title is null)
            {
                return null;
            }

            return new NewsArticle(
                article.Title,
                article.Version ?? string.Empty,
                article.Type ?? "release",
                article.Image?.Url is null ? string.Empty : ImageRoot + article.Image.Url,
                article.Body ?? string.Empty);
        }
        catch (Exception ex)
        {
            log.Warn("News", $"Статья недоступна: {ex.Message}");
            return null;
        }
    }

    private sealed class LauncherContentResponse
    {
        [JsonPropertyName("entries")]
        public List<LauncherContentEntry>? Entries { get; set; }
    }

    private sealed class LauncherContentArticle
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("image")]
        public LauncherContentImage? Image { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private sealed class LauncherContentEntry
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("image")]
        public LauncherContentImage? Image { get; set; }

        [JsonPropertyName("contentPath")]
        public string? ContentPath { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("date")]
        public DateTimeOffset Date { get; set; }
    }

    private sealed class LauncherContentImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
