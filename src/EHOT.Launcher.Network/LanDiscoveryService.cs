using System.Net;
using System.Net.Sockets;
using System.Text;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Network.Ice;
using Microsoft.Extensions.DependencyInjection;

namespace EHOT.Launcher.Network;

public sealed record LanWorld(string Motd, string Address, string AdvertisedPort, bool IsOpen);

/// <summary>
/// Обнаружение LAN-миров Minecraft: клиент и сервер обмениваются UDP-пакетами на порт 4445
/// формата "[MOTD]...[/MOTD][AD]...[/AD]". Помогает показать «мир открыт для сети» без ручного ввода порта.
/// </summary>
public interface ILanDiscoveryService
{
    Task<IReadOnlyList<LanWorld>> DiscoverAsync(TimeSpan window, CancellationToken ct = default);
}

public sealed class LanDiscoveryService(ILogService log) : ILanDiscoveryService
{
    private const int LanPort = 4445;

    public async Task<IReadOnlyList<LanWorld>> DiscoverAsync(TimeSpan window, CancellationToken ct = default)
    {
        var found = new Dictionary<string, LanWorld>(StringComparer.Ordinal);

        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanPort));
            udp.EnableBroadcast = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(window);

            // Отправляем запрос в широковещательный адрес — сервер Minecraft ответит рекламой мира.
            var probe = Encoding.UTF8.GetBytes("[MOTD]EHOT Probe[/MOTD][AD]25565[/AD]");
            await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, LanPort), timeout.Token).ConfigureAwait(false);

            while (!timeout.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var world = Parse(text, result.RemoteEndPoint.Address.ToString());
                if (world is not null)
                {
                    found[world.Motd + world.Address] = world;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение окна прослушивания
        }
        catch (SocketException ex)
        {
            log.Write(LogLevel2.Debug, "Network", $"LAN discovery недоступен: {ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            log.Warn("Network", $"Ошибка обнаружения LAN: {ex.Message}");
        }

        return found.Values.ToArray();
    }

    public static LanWorld? Parse(string payload, string fallbackAddress)
    {
        var motdStart = payload.IndexOf("[MOTD]", StringComparison.Ordinal);
        var motdEnd = payload.IndexOf("[/MOTD]", StringComparison.Ordinal);
        var adStart = payload.IndexOf("[AD]", StringComparison.Ordinal);
        var adEnd = payload.IndexOf("[/AD]", StringComparison.Ordinal);

        if (motdStart < 0 || motdEnd < 0 || adStart < 0 || adEnd < 0)
        {
            return null;
        }

        var motd = payload[(motdStart + 6)..motdEnd];
        var port = payload[(adStart + 4)..adEnd];
        return new LanWorld(motd, $"{fallbackAddress}:{port}", port, true);
    }
}

public static class NetworkModule
{
    /// <summary>Регистрирует сетевые сервисы; INetworkAdapter должен быть зарегистрирован отдельно.</summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddEhotNetworkFactory(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        RoomSessionOptions? options = null)
    {
        services.AddSingleton(options ?? new RoomSessionOptions());
        services.AddSingleton<IStunClient, Ice.StunClient>();
        services.AddSingleton<IControlPlaneClient, ControlPlaneClient>();
        services.AddSingleton<IRoomSession, RoomSession>();
        services.AddSingleton<ILanDiscoveryService, LanDiscoveryService>();
        return services;
    }
}
