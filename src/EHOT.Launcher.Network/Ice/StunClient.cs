using System.Net;
using System.Net.Sockets;
using EHOT.Launcher.Core.Diagnostics;

namespace EHOT.Launcher.Network.Ice;

public enum CandidateType
{
    Host = 0,
    ServerReflexive = 1,
    Relay = 2
}

public sealed record IceCandidate(CandidateType Type, IPEndPoint EndPoint, int Priority, string? RelatedAddress = null);

public sealed record StunResult(
    bool Success,
    IPEndPoint? PublicEndPoint,
    string? Error,
    bool IsSymmetricNat,
    TimeSpan RoundTrip);

/// <summary>
/// Минимальный STUN-клиент (RFC 5389, Binding Request) для определения внешнего адреса и типа NAT.
/// Используется только для NAT traversal; никакие данные не передаются третьим лицам, кроме STUN-запроса.
/// </summary>
public interface IStunClient
{
    Task<StunResult> QueryAsync(IPEndPoint server, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Определяет тип NAT двумя запросами к серверу с разными портами источника.</summary>
    Task<StunResult> DiscoverMappingAsync(IReadOnlyList<IPEndPoint> servers, CancellationToken ct = default);
}

public sealed class StunClient(ILogService log) : IStunClient
{
    private const uint MagicCookie = 0x2112A442;
    private static readonly byte[] BindingRequest = [0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42,
        0xE8, 0x7F, 0x1B, 0x9A, 0x3F, 0x1D, 0x5E, 0x2C,
        0x71, 0x43, 0xA2, 0x8B];

    public async Task<StunResult> QueryAsync(IPEndPoint server, TimeSpan timeout, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        using var udp = new UdpClient(server.AddressFamily);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        try
        {
            await udp.SendAsync(BindingRequest, server, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var response = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            var endpoint = ParseBindingResponse(response.Buffer, server);
            var elapsed = DateTimeOffset.UtcNow - started;

            return endpoint is null
                ? new StunResult(false, null, "STUN: не удалось разобрать ответ.", false, elapsed)
                : new StunResult(true, endpoint, null, false, elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StunResult(false, null, "STUN-сервер не ответил за отведённое время.", false, DateTimeOffset.UtcNow - started);
        }
        catch (SocketException ex)
        {
            log.Warn("STUN", $"Сетевая ошибка STUN: {ex.SocketErrorCode}");
            return new StunResult(false, null, $"STUN: {ex.SocketErrorCode}", false, DateTimeOffset.UtcNow - started);
        }
    }

    public async Task<StunResult> DiscoverMappingAsync(IReadOnlyList<IPEndPoint> servers, CancellationToken ct = default)
    {
        var results = new List<StunResult>();
        foreach (var server in servers.Take(3))
        {
            results.Add(await QueryAsync(server, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false));
        }

        var success = results.Where(r => r.Success && r.PublicEndPoint is not null).ToArray();
        if (success.Length == 0)
        {
            return results.FirstOrDefault() ?? new StunResult(false, null, "Нет доступных STUN-серверов.", false, TimeSpan.Zero);
        }

        // Если внешний порт меняется между серверами — вероятен symmetric NAT: прямое соединение маловероятно.
        var ports = success.Select(r => r.PublicEndPoint!.Port).Distinct().ToArray();
        var external = success[0].PublicEndPoint!;
        return new StunResult(true, external, null, ports.Length > 1, success[0].RoundTrip);
    }

    private static IPEndPoint? ParseBindingResponse(byte[] buffer, IPEndPoint server)
    {
        if (buffer.Length < 20)
        {
            return null;
        }

        var messageType = (buffer[0] << 8) | buffer[1];
        if (messageType != 0x0101)
        {
            return null; // не Binding Success Response
        }

        var length = (buffer[2] << 8) | buffer[3];
        var offset = 20;
        var end = Math.Min(buffer.Length, 20 + length);

        while (offset + 4 <= end)
        {
            var attrType = (buffer[offset] << 8) | buffer[offset + 1];
            var attrLen = (buffer[offset + 2] << 8) | buffer[offset + 3];
            var valueOffset = offset + 4;

            if (valueOffset + attrLen > end)
            {
                return null;
            }

            switch (attrType)
            {
                // XOR-MAPPED-ADDRESS
                case 0x0020:
                {
                    var family = buffer[valueOffset + 1];
                    var port = ((buffer[valueOffset + 2] << 8) | buffer[valueOffset + 3]) ^ (int)(MagicCookie >> 16);
                    if (family == 0x01)
                    {
                        var ipBytes = new byte[4];
                        for (var i = 0; i < 4; i++)
                        {
                            ipBytes[i] = (byte)(buffer[valueOffset + 4 + i] ^ (byte)(MagicCookie >> (24 - (8 * i))));
                        }

                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }

                    if (family == 0x02 && valueOffset + 20 <= end)
                    {
                        var ipBytes = new byte[16];
                        var cookieBytes = BitConverter.GetBytes(MagicCookie);
                        if (BitConverter.IsLittleEndian)
                        {
                            Array.Reverse(cookieBytes);
                        }

                        for (var i = 0; i < 16; i++)
                        {
                            var mask = i < 4 ? cookieBytes[i] : (byte)(buffer[4 + (i - 4)] ^ 0);
                            ipBytes[i] = (byte)(buffer[valueOffset + 4 + i] ^ mask);
                        }

                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }

                    break;
                }

                // MAPPED-ADDRESS (RFC 3489)
                case 0x0001:
                {
                    var family = buffer[valueOffset + 1];
                    var port = (buffer[valueOffset + 2] << 8) | buffer[valueOffset + 3];
                    if (family == 0x01)
                    {
                        var ipBytes = new byte[4];
                        Array.Copy(buffer, valueOffset + 4, ipBytes, 0, 4);
                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }

                    break;
                }
            }

            offset = valueOffset + attrLen + ((4 - (attrLen % 4)) % 4);
        }

        return null;
    }

}
