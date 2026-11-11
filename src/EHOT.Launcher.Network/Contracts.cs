namespace EHOT.Launcher.Network;

public enum RoomState
{
    Offline = 0,
    Creating = 1,
    WaitingForPeers = 2,
    Connected = 3,
    Reconnecting = 4,
    Closed = 5,
    Error = 6
}

public enum TransportKind
{
    None = 0,
    Direct = 1,
    NatTraversal = 2,
    Relay = 3
}

public sealed record RoomPeer(
    string MemberId,
    string DisplayName,
    string VirtualIp,
    string PublicKey,
    string? Endpoint,
    TransportKind Transport,
    int LatencyMs,
    DateTimeOffset LastSeen,
    bool IsOwner);

public sealed record RoomInfo(
    string RoomId,
    string JoinCode,
    string Name,
    string VirtualSubnet,
    string OwnerMemberId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaxMembers);

public sealed record RoomSnapshot(
    RoomState State,
    RoomInfo? Room,
    IReadOnlyList<RoomPeer> Peers,
    TransportKind Transport,
    int LatencyMs,
    long BytesSent,
    long BytesReceived,
    string? Error,
    string? TunnelStatusDetail)
{
    public static RoomSnapshot Offline { get; } = new(RoomState.Offline, null, [], TransportKind.None, 0, 0, 0, null, null);

    /// <summary>Адрес для Minecraft LAN/Direct Connect.</summary>
    public string? MinecraftAddress
    {
        get
        {
            if (Peers.Count == 0)
            {
                return null;
            }

            for (var i = 0; i < Peers.Count; i++)
            {
                if (Peers[i].VirtualIp.EndsWith(".1", StringComparison.Ordinal))
                {
                    return $"{Peers[i].VirtualIp}:25565";
                }
            }

            return $"{Peers[0].VirtualIp}:25565";
        }
    }
}

/// <summary>
/// Платформенный сетевой адаптер (TUN). Реализации обязаны честно сообщать состояние:
/// если системный механизм недоступен — TunState.Unavailable с причиной, а не «зелёный» статус.
/// </summary>
public interface INetworkAdapter
{
    string Id { get; }

    /// <summary>Доступен ли механизм на этой машине (без попытки поднять туннель).</summary>
    TunAvailability Availability { get; }

    /// <summary>Ключи генерирует бэкенд адаптера (приватный ключ не покидает устройство).</summary>
    Task<(string PublicKey, string PrivateKeyRef)> GenerateKeyPairAsync(CancellationToken ct = default);

    Task<TunOperationResult> StartAsync(TunnelConfig config, CancellationToken ct = default);

    Task StopAsync();

    Task<TunnelStats> GetStatsAsync(CancellationToken ct = default);

    event Action<TunnelStats>? StatsUpdated;
}

public sealed record TunAvailability(bool IsAvailable, string Detail, bool RequiresElevation)
{
    public static TunAvailability Available(string detail) => new(true, detail, false);

    public static TunAvailability Unavailable(string detail, bool requiresElevation = false) => new(false, detail, requiresElevation);
}

public sealed record TunnelPeerConfig(string PublicKey, string AllowedIp, string? Endpoint);

public sealed record TunnelConfig(
    string VirtualIp,
    string Subnet,
    string PrivateKeyRef,
    IReadOnlyList<TunnelPeerConfig> Peers,
    int ListenPort = 51820);

public sealed record TunOperationResult(bool Success, string? Error)
{
    public static TunOperationResult Ok() => new(true, null);

    public static TunOperationResult Fail(string error) => new(false, error);
}

public sealed record TunnelStats(long BytesSent, long BytesReceived, int HandshakeAgeSeconds, DateTimeOffset SampledAt)
{
    public static TunnelStats Empty { get; } = new(0, 0, int.MaxValue, DateTimeOffset.UtcNow);
}
