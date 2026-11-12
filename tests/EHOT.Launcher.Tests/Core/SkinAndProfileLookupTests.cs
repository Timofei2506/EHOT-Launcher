using EHOT.Launcher.Auth;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;
using Xunit;

namespace EHOT.Launcher.Tests.Core;

/// <summary>
/// Пиратский сценарий: ник → публичный UUID и скин официального игрока.
/// Здесь проверяется вся цепочка без сети (HTTP подменяется), чтобы поведение не сломалось молча.
/// </summary>
public sealed class SkinAndProfileLookupTests
{
    // Официальный скин 64×64: минимальный валидный PNG-заголовок нужного размера.
    private static byte[] SkinPng(int width = 64, int height = 64)
    {
        var bytes = new byte[64];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        return bytes;
    }

    [Fact]
    public void LooksLikeMinecraftSkin_AcceptsValidSizes()
    {
        Assert.True(PathGuard.LooksLikeMinecraftSkin(SkinPng(64, 64)));
        Assert.True(PathGuard.LooksLikeMinecraftSkin(SkinPng(64, 32)));
        Assert.False(PathGuard.LooksLikeMinecraftSkin(SkinPng(128, 128)));
        Assert.False(PathGuard.LooksLikeMinecraftSkin(SkinPng(64, 48)));
        Assert.False(PathGuard.LooksLikeMinecraftSkin(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task FetchSkin_ReturnsOfficialUuidAndTexture_WhenNickExists()
    {
        const string uuid = "069a79f444e94726a5befca90e38aaf5"; // Notch
        var http = new FakeHttp
        {
            Json =
            {
                [$"https://api.mojang.com/users/profiles/minecraft/Notch"] =
                    $"{{\"id\":\"{uuid}\",\"name\":\"Notch\"}}"
            },
            Bytes = { ["https://crafatar.com/skins/069a79f4-44e9-4726-a5be-fca90e38aaf5"] = SkinPng() }
        };

        var lookup = new ProfileLookupService(http, new NullLog());
        var result = await lookup.FetchSkinAsync("Notch", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", result.Value!.Uuid);
        Assert.Equal(64, result.Value.Png.Length >= 24 ? 64 : 0);
        Assert.Single(http.Downloaded);
    }

    [Fact]
    public async Task FetchSkin_ReturnsNull_WhenNickIsFree()
    {
        var http = new FakeHttp();
        http.NotFound.Add("https://api.mojang.com/users/profiles/minecraft/NobodyHere");
        var lookup = new ProfileLookupService(http, new NullLog());

        var result = await lookup.FetchSkinAsync("NobodyHere", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Empty(http.Downloaded);
    }

    [Fact]
    public async Task FetchSkin_RejectsFileThatIsNotASkin()
    {
        const string uuid = "069a79f444e94726a5befca90e38aaf5";
        var http = new FakeHttp
        {
            Json = { [$"https://api.mojang.com/users/profiles/minecraft/Notch"] = $"{{\"id\":\"{uuid}\",\"name\":\"Notch\"}}" },
            // «Скин» приходит, но это не PNG 64×64 — такой файл в игру подставлять нельзя.
            Bytes = { ["https://crafatar.com/skins/069a79f4-44e9-4726-a5be-fca90e38aaf5"] = SkinPng(128, 128) }
        };

        var lookup = new ProfileLookupService(http, new NullLog());
        var result = await lookup.FetchSkinAsync("Notch", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.Network, result.Code);
    }

    /// <summary>HTTP-заменитель: отдаёт заранее заданные ответы и запоминает скачивания.</summary>
    private sealed class FakeHttp : IHttpService
    {
        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.Ordinal);

        public HashSet<string> NotFound { get; } = new(StringComparer.Ordinal);

        public List<string> Downloaded { get; } = [];

        public Task<HttpResponseData> GetStringAsync(Uri uri, string? bearerToken = null, bool allowAnyHost = false,
            IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (NotFound.Contains(key))
            {
                return Task.FromResult(new HttpResponseData(System.Net.HttpStatusCode.NotFound, string.Empty, uri));
            }

            return Task.FromResult(Json.TryGetValue(key, out var body)
                ? new HttpResponseData(System.Net.HttpStatusCode.OK, body, uri)
                : new HttpResponseData(System.Net.HttpStatusCode.NotFound, string.Empty, uri));
        }

        public Task<TObject?> GetJsonAsync<TObject>(Uri uri, string? bearerToken = null, bool allowAnyHost = false,
            IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
            where TObject : class
            => Task.FromResult<TObject?>(null);

        public Task<HttpResponseData> SendJsonAsync(HttpMethod method, Uri uri, object? body, string? bearerToken = null,
            bool allowAnyHost = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TObject?> SendJsonAsync<TObject>(HttpMethod method, Uri uri, object? body, string? bearerToken = null,
            bool allowAnyHost = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
            where TObject : class
            => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(Uri uri, long? fromByte = null, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (!Bytes.TryGetValue(key, out var data))
            {
                throw new HttpRequestException($"Нет ответа для {key}");
            }

            Downloaded.Add(key);
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }

        public void Dispose() => Downloaded.Clear();
    }

    private sealed class NullLog : ILogService
    {
        public IReadOnlyList<LogEntry> Recent => [];

        public event Action<LogEntry>? EntryAdded { add { } remove { } }

        public void Write(LogLevel2 level, string category, string message, Exception? ex = null)
        {
        }

        public void Info(string category, string message)
        {
        }

        public void Warn(string category, string message, Exception? ex = null)
        {
        }

        public void Error(string category, string message, Exception? ex = null)
        {
        }

        public string CurrentLogFile => string.Empty;

        public Task FlushAsync() => Task.CompletedTask;
    }
}
