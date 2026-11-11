using System.Diagnostics;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Results;
using EHOT.Launcher.Network.Ice;

namespace EHOT.Launcher.Network;

public sealed record RoomSessionOptions
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PresenceRefresh { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan StatsRefresh { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxReconnectAttempts { get; init; } = 6;
}

public interface IRoomSession
{
    RoomSnapshot Snapshot { get; }

    event Action<RoomSnapshot>? SnapshotChanged;

    Task<OperationResult<RoomSnapshot>> CreateRoomAsync(string name, string displayName, CancellationToken ct = default);

    Task<OperationResult<RoomSnapshot>> JoinAsync(string joinCode, string displayName, CancellationToken ct = default);

    Task<OperationResult> LeaveAsync(CancellationToken ct = default);

    Task<OperationResult> KickMemberAsync(string memberId, CancellationToken ct = default);

    Task<OperationResult<string>> CopyAddressAsync(CancellationToken ct = default);
}

/// <summary>
/// Жизненный цикл комнаты: создание/подключение, heartbeat, обновление участников, переподключение,
/// реальные метрики (латентность, байты), управление участниками.
/// Состояние меняется только по фактам, полученным от сервера/адаптера.
/// </summary>
public sealed class RoomSession(
    IControlPlaneClient client,
    INetworkAdapter adapter,
    IStunClient stun,
    RoomSessionOptions options,
    ILogService log) : IRoomSession, IAsyncDisposable
{
    private RoomSnapshot _snapshot = RoomSnapshot.Offline;
    private CancellationTokenSource? _loop;
    private string? _memberId;
    private string _privateKeyRef = string.Empty;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RoomSnapshot Snapshot => _snapshot;

    public event Action<RoomSnapshot>? SnapshotChanged;

    public async Task<OperationResult<RoomSnapshot>> CreateRoomAsync(string name, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 48)
        {
            return OperationResult<RoomSnapshot>.Fail(ErrorCode.Validation, "Название комнаты: 1–48 символов.");
        }

        var availability = adapter.Availability;
        if (!availability.IsAvailable)
        {
            log.Warn("GameNetwork", "Адаптер недоступен: " + availability.Detail);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UpdateSnapshot(_snapshot with { State = RoomState.Creating, Error = null, TunnelStatusDetail = availability.Detail });

            var identity = await EnsureIdentityAsync(ct).ConfigureAwait(false);
            if (!identity.IsSuccess)
            {
                return Fail(identity.Code, identity.Message!);
            }

            var created = await client.CreateRoomAsync(new RoomCreationRequest(name), ct).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return Fail(created.Code, created.Message!);
            }

            var room = created.Value!;
            var joined = await JoinCoreAsync(room.JoinCode, displayName, ct).ConfigureAwait(false);
            return joined;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<RoomSnapshot>> JoinAsync(string joinCode, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            return OperationResult<RoomSnapshot>.Fail(ErrorCode.Validation, "Введите код приглашения.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UpdateSnapshot(_snapshot with { State = RoomState.Creating, Error = null });

            var identity = await EnsureIdentityAsync(ct).ConfigureAwait(false);
            if (!identity.IsSuccess)
            {
                return Fail(identity.Code, identity.Message!);
            }

            return await JoinCoreAsync(joinCode.Trim().ToUpperInvariant(), displayName, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult<RoomSnapshot>> JoinCoreAsync(string joinCode, string displayName, CancellationToken ct)
    {
        var keys = await adapter.GenerateKeyPairAsync(ct).ConfigureAwait(false);
        _privateKeyRef = keys.PrivateKeyRef;

        // Определяем внешний адрес для NAT traversal (не критично, если не удалось).
        var stunServers = new[]
        {
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("74.125.250.129"), 19302),
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("74.125.250.130"), 19302)
        };

        var mapping = await stun.DiscoverMappingAsync(stunServers, ct).ConfigureAwait(false);
        var endpoint = mapping.Success && mapping.PublicEndPoint is not null
            ? mapping.PublicEndPoint.ToString()
            : null;

        var join = await client.JoinRoomAsync(joinCode, new PeerRegistration(displayName, keys.PublicKey, endpoint), ct).ConfigureAwait(false);
        if (!join.IsSuccess)
        {
            return Fail(join.Code, join.Message!);
        }

        var data = join.Value!;
        _memberId = data.MyMemberId;

        var transport = DetermineTransport(data.Peers);
        var tunnel = await StartTunnelAsync(data, ct).ConfigureAwait(false);

        var peers = data.Peers.Count == 0
            ? new List<RoomPeer> { new(data.MyMemberId, displayName, data.MyVirtualIp, keys.PublicKey, endpoint, TransportKind.Direct, 0, DateTimeOffset.UtcNow, true) }
            : data.Peers.ToList();

        var snapshot = new RoomSnapshot(
            peers.Count <= 1 ? RoomState.WaitingForPeers : RoomState.Connected,
            data.Room,
            peers,
            transport,
            mapping.Success ? (int)mapping.RoundTrip.TotalMilliseconds : 0,
            0,
            0,
            tunnel.Error,
            tunnel.Detail);

        UpdateSnapshot(snapshot);
        StartLoops(data.Room.RoomId);

        log.Info("GameNetwork", $"Комната {data.Room.RoomId}: участников {peers.Count}, транспорт {transport}, адрес {snapshot.MinecraftAddress}");
        return OperationResult<RoomSnapshot>.Ok(snapshot);
    }

    public async Task<OperationResult> LeaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;

            if (_snapshot.Room is not null)
            {
                await client.LeaveRoomAsync(_snapshot.Room.RoomId, ct).ConfigureAwait(false);
            }

            await adapter.StopAsync().ConfigureAwait(false);
            UpdateSnapshot(RoomSnapshot.Offline);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            UpdateSnapshot(_snapshot with { State = RoomState.Error, Error = ex.Message });
            return OperationResult.FromException(ex, ErrorCode.Network);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> KickMemberAsync(string memberId, CancellationToken ct = default)
    {
        if (_snapshot.Room is null)
        {
            return OperationResult.Fail(ErrorCode.Validation, "Нет активной комнаты.");
        }

        var result = await client.KickMemberAsync(_snapshot.Room.RoomId, memberId, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            var peers = _snapshot.Peers.Where(p => p.MemberId != memberId).ToArray();
            UpdateSnapshot(_snapshot with { Peers = peers });
            log.Info("GameNetwork", $"Участник {memberId} удалён из комнаты.");
        }

        return result;
    }

    public Task<OperationResult<string>> CopyAddressAsync(CancellationToken ct = default)
    {
        var address = _snapshot.MinecraftAddress;
        return Task.FromResult(address is null
            ? OperationResult<string>.Fail(ErrorCode.NotFound, "Адрес недоступен: нет подключения к комнате.")
            : OperationResult<string>.Ok(address));
    }

    private async Task<(bool IsSuccess, ErrorCode Code, string? Message)> EnsureIdentityAsync(CancellationToken ct)
    {
        if (!client.IsConfigured)
        {
            return (false, ErrorCode.Configuration,
                "Game Network не настроена: укажите адрес контрольного сервера в Настройках → Сеть.");
        }

        var keys = await adapter.GenerateKeyPairAsync(ct).ConfigureAwait(false);
        var register = await client.RegisterIdentityAsync(keys.PublicKey, ct).ConfigureAwait(false);
        return register.IsSuccess
            ? (true, ErrorCode.Unknown, null)
            : (false, register.Code, register.Message);
    }

    private async Task<(string? Error, string? Detail)> StartTunnelAsync(RoomJoinResult data, CancellationToken ct)
    {
        if (!adapter.Availability.IsAvailable)
        {
            return (adapter.Availability.Detail, "Туннель недоступен — используется relay/прямое соединение.");
        }

        var tunnelConfig = new TunnelConfig(
            data.MyVirtualIp,
            data.Room.VirtualSubnet,
            _privateKeyRef,
            data.Peers.Select(p => new TunnelPeerConfig(p.PublicKey, p.VirtualIp + "/32", p.Endpoint)).ToArray());

        var start = await adapter.StartAsync(tunnelConfig, ct).ConfigureAwait(false);
        return start.Success ? (null, null) : (start.Error, adapter.Availability.Detail);
    }

    private void StartLoops(string roomId)
    {
        _loop = new CancellationTokenSource();
        var token = _loop.Token;

        _ = Task.Run(async () =>
        {
            var attempts = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(options.HeartbeatInterval, token).ConfigureAwait(false);

                    var heartbeat = await client.HeartbeatAsync(roomId, token).ConfigureAwait(false);
                    if (!heartbeat.IsSuccess)
                    {
                        attempts++;
                        UpdateSnapshot(_snapshot with { State = RoomState.Reconnecting, Error = heartbeat.Message });

                        if (attempts >= options.MaxReconnectAttempts)
                        {
                            UpdateSnapshot(_snapshot with { State = RoomState.Error, Error = "Соединение с сервером комнаты потеряно." });
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempts))), token).ConfigureAwait(false);
                        continue;
                    }

                    attempts = 0;
                    var refresh = await client.GetRoomAsync(roomId, token).ConfigureAwait(false);
                    if (refresh.IsSuccess)
                    {
                        var stats = await adapter.GetStatsAsync(token).ConfigureAwait(false);
                        UpdateSnapshot(_snapshot with
                        {
                            State = refresh.Value!.Peers.Count <= 1 ? RoomState.WaitingForPeers : RoomState.Connected,
                            Peers = refresh.Value.Peers,
                            BytesSent = stats.BytesSent,
                            BytesReceived = stats.BytesReceived,
                            Error = null
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.Warn("GameNetwork", "Ошибка цикла присутствия: " + ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    private static TransportKind DetermineTransport(IReadOnlyList<RoomPeer> peers)
    {
        if (peers.Count == 0)
        {
            return TransportKind.None;
        }

        return peers.All(p => p.Transport == TransportKind.Direct)
            ? TransportKind.Direct
            : peers.Any(p => p.Transport == TransportKind.Relay) ? TransportKind.Relay : TransportKind.NatTraversal;
    }

    private void UpdateSnapshot(RoomSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private OperationResult<RoomSnapshot> Fail(ErrorCode code, string message)
    {
        UpdateSnapshot(_snapshot with { State = RoomState.Error, Error = message });
        return OperationResult<RoomSnapshot>.Fail(code, message);
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
