using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EHOT.Launcher.Tests.API;

/// <summary>
/// Интеграционные тесты control-plane: приложение поднимается целиком (WebApplicationFactory)
/// на SQLite-базе во временном файле. Проверяется контракт, на который опирается клиент лаунчера:
/// регистрация identity, создание комнаты, вступление по коду приглашения, защита эндпоинтов.
/// </summary>
public sealed class ControlPlaneApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath;

    public ControlPlaneApiTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "ehot-api-" + Guid.NewGuid().ToString("N") + ".db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.UseSetting("ConnectionStrings:Postgres", $"Data Source={_dbPath}");
            builder.UseSetting("Auth:JwtSecret", new string('j', 48));
            builder.UseSetting("Auth:InviteSecret", new string('s', 48));
            builder.UseSetting(WebHostDefaults.DetailedErrorsKey, "true");
        });
    }

    public async ValueTask InitializeAsync()
    {
        // Прогреваем приложение и создаём схему.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EHOT.Launcher.API.Data.ControlPlaneDbContext>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // временная база — не критично
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewPublicKey() => Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    private static async Task<(string Token, Guid IdentityId)> RegisterAsync(HttpClient client, string? displayName = null)
    {
        var response = await client.PostAsJsonAsync(
            "/v1/identity/register",
            new { publicKey = NewPublicKey(), displayName },
            Token);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        return (json.GetProperty("token").GetString()!, json.GetProperty("identityId").GetGuid());
    }

    private static HttpClient Authorized(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Meta_IsPublicAndDescribesContract()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/v1/meta", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.False(string.IsNullOrWhiteSpace(json.ToString()));
    }

    [Fact]
    public async Task Register_ReturnsTokenAndRejectsShortKey()
    {
        var client = _factory.CreateClient();

        var (token, identityId) = await RegisterAsync(client, "Тестер");
        Assert.NotEqual(Guid.Empty, identityId);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var bad = await client.PostAsJsonAsync("/v1/identity/register", new { publicKey = "short" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Rooms_RequireAuthentication()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/rooms", new { name = "Без токена" }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Room_CreateJoinAndReadState()
    {
        var owner = Authorized(_factory.CreateClient(), (await RegisterAsync(_factory.CreateClient())).Token);
        var guest = Authorized(_factory.CreateClient(), (await RegisterAsync(_factory.CreateClient())).Token);

        var created = await owner.PostAsJsonAsync("/v1/rooms", new { name = "Тестовая комната" }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));

        var room = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var roomId = room.GetProperty("roomId").GetGuid();
        var joinCode = room.GetProperty("joinCode").GetString();

        Assert.False(string.IsNullOrWhiteSpace(joinCode));
        Assert.Equal(8, joinCode!.Length);

        var joined = await guest.PostAsJsonAsync(
            "/v1/rooms/join",
            new { joinCode, registration = new { publicKey = NewPublicKey(), displayName = "Гость" } },
            Token);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        var state = await owner.GetAsync($"/v1/rooms/{roomId}", Token);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        var stateJson = await state.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(stateJson.GetProperty("peers").GetArrayLength() >= 1);
        Assert.Equal(roomId, stateJson.GetProperty("roomId").GetGuid());
    }

    [Fact]
    public async Task Join_WithUnknownCode_ReturnsNotFound()
    {
        var client = Authorized(_factory.CreateClient(), (await RegisterAsync(_factory.CreateClient())).Token);

        var response = await client.PostAsJsonAsync(
            "/v1/rooms/join",
            new { joinCode = "ZZZZ9999", registration = new { publicKey = NewPublicKey() } },
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Room_CanBeClosedByOwnerAndIsNoLongerVisible()
    {
        var owner = Authorized(_factory.CreateClient(), (await RegisterAsync(_factory.CreateClient())).Token);

        var created = await owner.PostAsJsonAsync("/v1/rooms", new { name = "Закрываемая" }, Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var roomId = room.GetProperty("roomId").GetGuid();

        var deleted = await owner.DeleteAsync($"/v1/rooms/{roomId}", Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var state = await owner.GetAsync($"/v1/rooms/{roomId}", Token);
        Assert.True(state.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden);
    }
}
