using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EHOT.Launcher.Core.Diagnostics;

namespace EHOT.Launcher.Game.Servers;

public sealed record ServerStatus(
    bool Online,
    int? PlayersOnline,
    int? PlayersMax,
    string Version,
    string Description,
    long LatencyMs,
    string? Error);

public interface IServerPinger
{
    Task<ServerStatus> PingAsync(string host, int port, CancellationToken ct = default);
}

/// <summary>Minecraft Server List Ping (Java Edition) — реальный онлайн/игроки/версия.</summary>
public sealed class MinecraftServerPinger(ILogService log) : IServerPinger
{
    private const int ProtocolVersion = 767; // серверы отвечают на статус любому protocol version

    public async Task<ServerStatus> PingAsync(string host, int port, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            using var stream = client.GetStream();

            await WritePacketAsync(stream, BuildHandshake(host, port), timeout.Token).ConfigureAwait(false);
            await WritePacketAsync(stream, [0x00], timeout.Token).ConfigureAwait(false); // status request

            var jsonBytes = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            using var packet = new MemoryStream(jsonBytes);
            using var reader = new BinaryReader(packet, Encoding.UTF8, leaveOpen: true);
            ReadVarInt(reader); // packet id = 0x00
            var jsonLength = ReadVarInt(reader);
            var json = Encoding.UTF8.GetString(reader.ReadBytes(jsonLength));

            sw.Stop();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int? online = null, max = null;
            if (root.TryGetProperty("players", out var players))
            {
                if (players.TryGetProperty("online", out var onlineEl) && onlineEl.ValueKind == JsonValueKind.Number)
                {
                    online = onlineEl.GetInt32();
                }

                if (players.TryGetProperty("max", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
                {
                    max = maxEl.GetInt32();
                }
            }

            var version = root.TryGetProperty("version", out var versionEl)
                && versionEl.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;

            var description = root.TryGetProperty("description", out var descriptionEl)
                ? ExtractDescription(descriptionEl)
                : string.Empty;

            return new ServerStatus(true, online, max, version, description, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or EndOfStreamException or IOException or JsonException or OperationCanceledException)
        {
            sw.Stop();
            log.Write(LogLevel2.Debug, "Servers", $"Ping {host}:{port} не удался: {ex.Message}");
            return new ServerStatus(false, null, null, string.Empty, string.Empty, sw.ElapsedMilliseconds,
                ex is OperationCanceledException ? "таймаут" : "недоступен");
        }
    }

    private static byte[] BuildHandshake(string host, int port)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0x00); // handshake packet id
        WriteVarInt(writer, ProtocolVersion);
        var hostBytes = Encoding.UTF8.GetBytes(host);
        WriteVarInt(writer, hostBytes.Length);
        writer.Write(hostBytes);
        writer.Write((byte)((port >> 8) & 0xFF));
        writer.Write((byte)(port & 0xFF));
        WriteVarInt(writer, 1); // next state: status
        writer.Flush();
        return ms.ToArray();
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WriteVarInt(writer, payload.Length);
        writer.Flush();
        await stream.WriteAsync(ms.ToArray(), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length is < 0 or > 2_000_000)
        {
            throw new IOException($"Некорректная длина пакета статуса: {length}");
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        var value = 0;
        for (var position = 0; position < 5; position++)
        {
            var one = new byte[1];
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            value |= (one[0] & 0x7F) << (7 * position);
            if ((one[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new IOException("VarInt длиннее 5 байт");
    }

    private static int ReadVarInt(BinaryReader reader)
    {
        var value = 0;
        for (var position = 0; position < 5; position++)
        {
            var one = reader.ReadByte();
            value |= (one & 0x7F) << (7 * position);
            if ((one & 0x80) == 0)
            {
                return value;
            }
        }

        throw new IOException("VarInt длиннее 5 байт");
    }

    private static void WriteVarInt(BinaryWriter writer, int value)
    {
        var unsigned = (uint)value;
        while (true)
        {
            var temp = (byte)(unsigned & 0x7F);
            unsigned >>= 7;
            if (unsigned != 0)
            {
                writer.Write((byte)(temp | 0x80));
            }
            else
            {
                writer.Write(temp);
                return;
            }
        }
    }

    private static readonly Regex FormatCodes = new(@"§[0-9A-FK-ORa-fk-or]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ExtractDescription(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return FormatCodes.Replace(element.GetString() ?? string.Empty, string.Empty).Trim();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        AppendText(element, sb);
        return FormatCodes.Replace(sb.ToString(), string.Empty).Trim();

        static void AppendText(JsonElement node, StringBuilder target)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                target.Append(node.GetString());
                return;
            }

            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                target.Append(text.GetString());
            }

            if (node.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in extra.EnumerateArray())
                {
                    AppendText(child, target);
                }
            }
        }
    }
}
