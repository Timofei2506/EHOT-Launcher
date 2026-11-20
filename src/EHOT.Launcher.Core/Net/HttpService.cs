using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using EHOT.Launcher.Core.Security;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Core.Net;

public sealed record HttpSettings
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan LongDownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxRetries { get; init; } = 3;
    public int MaxParallelRequests { get; init; } = 8;
    public string UserAgent { get; init; } = "EHOT-Launcher/0.1.0 (contact: support@ehot.example)";
    public long MaxJsonBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Домены, откуда разрешено скачивать исполняемые/игровые файлы.</summary>
    public IReadOnlySet<string> AllowedDownloadHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "piston-data.mojang.com",
        "piston-meta.mojang.com",
        "launchermeta.mojang.com",
        "launchercontent.mojang.com",
        "resources.download.minecraft.net",
        "libraries.minecraft.net",
        "api.mojang.com",
        "sessionserver.mojang.com",
        // Официальные текстуры скинов и их публичный рендер: нужны для пиратского режима
        // (ник → скин официального игрока).
        "textures.minecraft.net",
        "crafatar.com",
        "cdn.modrinth.com",
        "api.modrinth.com",
        "cdn-raw.modrinth.com",
        "api.curseforge.com",
        "edge.forgecdn.net",
        "media.forgecdn.net",
        "maven.minecraftforge.net",
        "maven.neoforged.net",
        "meta.fabricmc.net",
        "maven.fabricmc.net",
        "meta.quiltmc.org",
        "maven.quiltmc.org",
        "api.adoptium.net",
        // OptiFine: зеркало BMCLAPI отдаёт файлы напрямую, optifine.net — через страницу adloadx.
        "bmclapi2.bangbang93.com",
        "optifine.net",
        "github.com",
        "objects.githubusercontent.com",
        "launchercontent.mojang.com",
        "ehot.example"
    };

    public static HttpSettings Default { get; } = new();
}

public sealed record HttpResponseData(HttpStatusCode StatusCode, string Content, Uri? FinalUri)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
}

/// <summary>HTTP-клиент проекта: единый User-Agent, таймауты, retry с Retry-After, ограничение параллелизма, проверка домена.</summary>
public interface IHttpService
{
    Task<HttpResponseData> GetStringAsync(Uri uri, string? bearerToken = null, bool allowAnyHost = false,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default);

    Task<TObject?> GetJsonAsync<TObject>(Uri uri, string? bearerToken = null, bool allowAnyHost = false,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        where TObject : class;

    Task<HttpResponseData> SendJsonAsync(HttpMethod method, Uri uri, object? body, string? bearerToken = null,
        bool allowAnyHost = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default);

    Task<TObject?> SendJsonAsync<TObject>(HttpMethod method, Uri uri, object? body, string? bearerToken = null,
        bool allowAnyHost = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        where TObject : class;

    /// <summary>Потоковая загрузка с проверкой домена и поддержкой Range.</summary>
    Task<Stream> OpenReadAsync(Uri uri, long? fromByte = null, CancellationToken ct = default);
}

public sealed class HttpService : IHttpService, IDisposable
{
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate;
    private readonly HttpSettings _settings;
    private readonly ILogger<HttpService> _logger;

    public HttpService(HttpSettings settings, ILogger<HttpService> logger)
    {
        _settings = settings;
        _logger = logger;
        _gate = new SemaphoreSlim(_settings.MaxParallelRequests);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = _settings.MaxParallelRequests,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            EnableMultipleHttp2Connections = false,
            UseCookies = false
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = _settings.LongDownloadTimeout
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_settings.UserAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<HttpResponseData> GetStringAsync(Uri uri, string? bearerToken = null, bool allowAnyHost = false,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        EnsureHostAllowed(uri, allowAnyHost);
        using var response = await SendWithRetryAsync(() => CreateRequest(HttpMethod.Get, uri, bearerToken, null, headers), ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new HttpResponseData(response.StatusCode, content, response.RequestMessage?.RequestUri);
    }

    public async Task<TObject?> GetJsonAsync<TObject>(Uri uri, string? bearerToken = null, bool allowAnyHost = false,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        where TObject : class
    {
        var data = await GetStringAsync(uri, bearerToken, allowAnyHost, headers, ct).ConfigureAwait(false);
        if (!data.IsSuccess)
        {
            _logger.LogDebug("GET {Uri} → {Status}", uri, data.StatusCode);
            return null;
        }

        if (data.Content.Length > _settings.MaxJsonBytes)
        {
            throw new InvalidOperationException($"Ответ {uri} превышает допустимый размер JSON.");
        }

        return System.Text.Json.JsonSerializer.Deserialize<TObject>(data.Content);
    }

    public async Task<HttpResponseData> SendJsonAsync(HttpMethod method, Uri uri, object? body, string? bearerToken = null,
        bool allowAnyHost = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        EnsureHostAllowed(uri, allowAnyHost);
        using var response = await SendWithRetryAsync(() => CreateRequest(method, uri, bearerToken, body, headers), ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new HttpResponseData(response.StatusCode, content, response.RequestMessage?.RequestUri);
    }

    public async Task<TObject?> SendJsonAsync<TObject>(HttpMethod method, Uri uri, object? body, string? bearerToken = null,
        bool allowAnyHost = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        where TObject : class
    {
        var data = await SendJsonAsync(method, uri, body, bearerToken, allowAnyHost, headers, ct).ConfigureAwait(false);
        return data.IsSuccess
            ? System.Text.Json.JsonSerializer.Deserialize<TObject>(data.Content)
            : null;
    }

    public async Task<Stream> OpenReadAsync(Uri uri, long? fromByte = null, CancellationToken ct = default)
    {
        EnsureHostAllowed(uri, allowAnyHost: false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Clear();
            if (fromByte is > 0)
            {
                request.Headers.Range = new RangeHeaderValue(fromByte.Value, null);
            }

            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
            {
                response.Dispose();
                throw new HttpRequestException($"Загрузка {uri} вернула {(int)response.StatusCode}.", null, response.StatusCode);
            }

            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _settings.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var request = factory();
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                if (ShouldRetry(response.StatusCode) && attempt < _settings.MaxRetries)
                {
                    var delay = RetryDelay(response, attempt);
                    response.Dispose();
                    _logger.LogDebug("Повтор HTTP через {Delay} (попытка {Attempt})", delay, attempt + 1);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                _logger.LogTrace("HTTP {Method} {Uri} → {Status} за {Ms} мс",
                    request.Method, request.RequestUri, (int)response.StatusCode, sw.ElapsedMilliseconds);
                return response;
            }
            catch (HttpRequestException ex) when (attempt < _settings.MaxRetries)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < _settings.MaxRetries)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new HttpRequestException("HTTP-запрос не удался.");
    }

    private static bool ShouldRetry(HttpStatusCode code)
        => code is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }

        return TimeSpan.FromMilliseconds(Math.Min(8000, 400 * Math.Pow(2, attempt)));
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string? bearerToken,
        object? body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        // Дополнительные заголовки (например, x-api-key для CurseForge) — только через общий HttpClient,
        // чтобы не терять пул соединений и политики повторов.
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body, JsonDefaults.Options),
                System.Text.Encoding.UTF8,
                "application/json");
        }

        return request;
    }

    private void EnsureHostAllowed(Uri uri, bool allowAnyHost)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new SecurityException($"Небезопасная схема URL: {uri.Scheme}. Разрешён только HTTPS.");
        }

        if (allowAnyHost)
        {
            return;
        }

        if (!_settings.AllowedDownloadHosts.Contains(uri.Host))
        {
            throw new SecurityException($"Домен '{uri.Host}' не входит в список разрешённых источников.");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Единые JSON-опции проекта.</summary>
public static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false
    };

    /// <summary>Отдельные опции для внешних API (Mojang/Modrinth) — без переименования полей при записи.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions External = new(Options)
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true
    };
}
