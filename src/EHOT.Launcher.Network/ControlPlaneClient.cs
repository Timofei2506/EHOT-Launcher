using System.Net;
using System.Text.Json.Serialization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Net;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Core.Security;

namespace EHOT.Launcher.Network;

public sealed record ControlPlaneOptions
{
    public string BaseUrl { get; init; } = "https://api.ehot.example";
    public string? IdentityToken { get; init; }
    public string ClientVersion { get; init; } = "0.1.0";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !BaseUrl.Contains("example", StringComparison.OrdinalIgnoreCase);
}

public sealed record RoomCreationRequest(string Name, int MaxMembers = 8, bool AllowRelay = true);

public sealed record RoomJoinResult(
    RoomInfo Room,
    string MyMemberId,
    string MyVirtualIp,
    IReadOnlyList<RoomPeer> Peers,
    int Revision,
    string Signature);

public sealed record PeerRegistration(string DisplayName, string PublicKey, string? Endpoint);

public interface IControlPlaneClient
{
    bool IsConfigured { get; }

    Task<OperationResult<string>> RegisterIdentityAsync(string publicKey, CancellationToken ct = default);

    Task<OperationResult<RoomInfo>> CreateRoomAsync(RoomCreationRequest request, CancellationToken ct = default);

    Task<OperationResult<RoomJoinResult>> JoinRoomAsync(string inviteCode, PeerRegistration registration, CancellationToken ct = default);

    Task<OperationResult<RoomJoinResult>> GetRoomAsync(string roomId, CancellationToken ct = default);

    Task<OperationResult> HeartbeatAsync(string roomId, CancellationToken ct = default);

    Task<OperationResult> KickMemberAsync(string roomId, string memberId, CancellationToken ct = default);

    Task<OperationResult> LeaveRoomAsync(string roomId, CancellationToken ct = default);

    Task<OperationResult> CloseRoomAsync(string roomId, CancellationToken ct = default);

    Task<OperationResult<int>> PingAsync(CancellationToken ct = default);
}

/// <summary>
/// REST-клиент контрольного сервера EHOT Game Network.
/// Если сервер не настроен/недоступен, методы возвращают честную ошибку Configuration/Network —
/// UI показывает «Game Network недоступна», без имитации комнат.
/// </summary>
public sealed class ControlPlaneClient(
    IHttpService http,
    ControlPlaneOptions options,
    ILogService log) : IControlPlaneClient
{
    private string? _token = options.IdentityToken;

    public bool IsConfigured => options.IsConfigured;

    public async Task<OperationResult<string>> RegisterIdentityAsync(string publicKey, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return OperationResult<string>.Fail(ErrorCode.Configuration, NotConfiguredMessage);
        }

        var response = await SendAsync<IdentityResponse>(HttpMethod.Post, "/v1/identity/register", new { publicKey }, ct).ConfigureAwait(false);
        if (!response.IsSuccess || response.Value?.Token is null)
        {
            return OperationResult<string>.Fail(response.Code, response.Message ?? "Регистрация identity не удалась.");
        }

        _token = response.Value.Token;
        return OperationResult<string>.Ok(_token);
    }

    public async Task<OperationResult<RoomInfo>> CreateRoomAsync(RoomCreationRequest request, CancellationToken ct = default)
    {
        var response = await SendAsync<RoomInfoResponse>(HttpMethod.Post, "/v1/rooms", request, ct).ConfigureAwait(false);
        if (!response.IsSuccess || response.Value is null)
        {
            return OperationResult<RoomInfo>.Fail(response.Code, response.Message ?? "Не удалось создать комнату.");
        }

        var v = response.Value;
        return OperationResult<RoomInfo>.Ok(new RoomInfo(
            v.RoomId, v.JoinCode, v.Name, v.VirtualSubnet, v.OwnerMemberId,
            v.CreatedAt, v.ExpiresAt, v.MaxMembers));
    }

    public async Task<OperationResult<RoomJoinResult>> JoinRoomAsync(string inviteCode, PeerRegistration registration, CancellationToken ct = default)
    {
        var response = await SendAsync<RoomJoinResponse>(
            HttpMethod.Post, "/v1/rooms/join", new { joinCode = inviteCode, registration }, ct).ConfigureAwait(false);

        if (!response.IsSuccess || response.Value is null)
        {
            return OperationResult<RoomJoinResult>.Fail(response.Code, response.Message ?? "Не удалось подключиться к комнате.");
        }

        return OperationResult<RoomJoinResult>.Ok(MapJoin(response.Value));
    }

    public async Task<OperationResult<RoomJoinResult>> GetRoomAsync(string roomId, CancellationToken ct = default)
    {
        var response = await SendAsync<RoomJoinResponse>(HttpMethod.Get, $"/v1/rooms/{roomId}", null, ct).ConfigureAwait(false);
        return response.IsSuccess && response.Value is not null
            ? OperationResult<RoomJoinResult>.Ok(MapJoin(response.Value))
            : OperationResult<RoomJoinResult>.Fail(response.Code, response.Message ?? "Комната недоступна.");
    }

    public async Task<OperationResult> HeartbeatAsync(string roomId, CancellationToken ct = default)
        => (await SendAsync<EmptyResponse>(HttpMethod.Post, $"/v1/rooms/{roomId}/heartbeat", null, ct).ConfigureAwait(false)).ToOperationResult();

    public async Task<OperationResult> KickMemberAsync(string roomId, string memberId, CancellationToken ct = default)
        => (await SendAsync<EmptyResponse>(HttpMethod.Delete, $"/v1/rooms/{roomId}/members/{memberId}", null, ct).ConfigureAwait(false)).ToOperationResult();

    public async Task<OperationResult> LeaveRoomAsync(string roomId, CancellationToken ct = default)
        => (await SendAsync<EmptyResponse>(HttpMethod.Delete, $"/v1/rooms/{roomId}/members/me", null, ct).ConfigureAwait(false)).ToOperationResult();

    public async Task<OperationResult> CloseRoomAsync(string roomId, CancellationToken ct = default)
        => (await SendAsync<EmptyResponse>(HttpMethod.Delete, $"/v1/rooms/{roomId}", null, ct).ConfigureAwait(false)).ToOperationResult();

    public async Task<OperationResult<int>> PingAsync(CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var response = await SendAsync<EmptyResponse>(HttpMethod.Get, "/healthz", null, ct).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return OperationResult<int>.Fail(response.Code, response.Message ?? "Сервер недоступен.");
        }

        return OperationResult<int>.Ok((int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }

    private async Task<OperationResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        where T : class, new()
    {
        if (!IsConfigured)
        {
            return OperationResult<T>.Fail(ErrorCode.Configuration, NotConfiguredMessage);
        }

        try
        {
            var uri = new Uri(options.BaseUrl.TrimEnd('/') + path);
            var data = await http.SendJsonAsync(method, uri, body, _token, allowAnyHost: true, ct: ct).ConfigureAwait(false);

            if (!data.IsSuccess)
            {
                var code = data.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => ErrorCode.Unauthorized,
                    HttpStatusCode.Forbidden => ErrorCode.Forbidden,
                    HttpStatusCode.NotFound => ErrorCode.NotFound,
                    HttpStatusCode.TooManyRequests => ErrorCode.RateLimited,
                    HttpStatusCode.BadRequest => ErrorCode.Validation,
                    _ => ErrorCode.Network
                };

                var message = ExtractError(data.Content) ?? $"Сервер вернул {(int)data.StatusCode}.";
                return OperationResult<T>.Fail(code, message);
            }

            if (string.IsNullOrWhiteSpace(data.Content))
            {
                return OperationResult<T>.Ok(new T());
            }

            var parsed = System.Text.Json.JsonSerializer.Deserialize<T>(data.Content, JsonDefaults.External);
            return parsed is null
                ? OperationResult<T>.Fail(ErrorCode.Validation, "Пустой ответ сервера.")
                : OperationResult<T>.Ok(parsed);
        }
        catch (Exception ex)
        {
            log.Warn("Network", $"Ошибка запроса к control plane {path}", ex);
            return OperationResult<T>.FromException(ex, ErrorCode.Network);
        }
    }

    private static string? ExtractError(string content)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                return err.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    private static string NotConfiguredMessage =>
        "Game Network не настроена: в Настройках укажите адрес контрольного сервера EHOT " +
        "(например, https://api.ehot.example — замените на собственный). Комнаты создаются только на реальном сервере.";

    private static RoomJoinResult MapJoin(RoomJoinResponse v) => new(
        new RoomInfo(v.RoomId, string.Empty, v.Name, v.VirtualSubnet, v.OwnerMemberId, v.CreatedAt, v.ExpiresAt, v.MaxMembers),
        v.MyMemberId,
        v.MyVirtualIp,
        v.Peers.Select(p => new RoomPeer(
            p.MemberId, p.DisplayName, p.VirtualIp, p.PublicKey, p.Endpoint,
            Enum.TryParse<TransportKind>(p.Transport, true, out var t) ? t : TransportKind.None,
            p.LatencyMs, p.LastSeen, p.IsOwner)).ToArray(),
        v.Revision,
        v.Signature);

    private sealed class EmptyResponse;

    private sealed class IdentityResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    private sealed class RoomInfoResponse
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("joinCode")]
        public string JoinCode { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("virtualSubnet")]
        public string VirtualSubnet { get; set; } = "10.64.0.1/24";

        [JsonPropertyName("ownerMemberId")]
        public string OwnerMemberId { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("maxMembers")]
        public int MaxMembers { get; set; }
    }

    private sealed class RoomJoinResponse
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("virtualSubnet")]
        public string VirtualSubnet { get; set; } = "10.64.0.1/24";

        [JsonPropertyName("ownerMemberId")]
        public string OwnerMemberId { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("maxMembers")]
        public int MaxMembers { get; set; }

        [JsonPropertyName("myMemberId")]
        public string MyMemberId { get; set; } = string.Empty;

        [JsonPropertyName("myVirtualIp")]
        public string MyVirtualIp { get; set; } = string.Empty;

        [JsonPropertyName("peers")]
        public List<PeerResponse> Peers { get; set; } = [];

        [JsonPropertyName("revision")]
        public int Revision { get; set; }

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }

    private sealed class PeerResponse
    {
        [JsonPropertyName("memberId")]
        public string MemberId { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("virtualIp")]
        public string VirtualIp { get; set; } = string.Empty;

        [JsonPropertyName("publicKey")]
        public string PublicKey { get; set; } = string.Empty;

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }

        [JsonPropertyName("transport")]
        public string? Transport { get; set; }

        [JsonPropertyName("latencyMs")]
        public int LatencyMs { get; set; }

        [JsonPropertyName("lastSeen")]
        public DateTimeOffset LastSeen { get; set; }

        [JsonPropertyName("isOwner")]
        public bool IsOwner { get; set; }
    }
}
