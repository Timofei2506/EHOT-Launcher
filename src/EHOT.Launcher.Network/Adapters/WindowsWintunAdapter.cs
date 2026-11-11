using System.Net.Http.Json;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Network.Adapters;

/// <summary>
/// Windows-адаптер: управление TUN-интерфейсом выполняет отдельный привилегированный помощник
/// (EHOT.Network.Service), лаунчер общается с ним по локальному HTTP/pipe-каналу с одноразовым токеном.
///
/// Почему так: WireGuard-туннель требует создания виртуального адаптера (Wintun) и изменения таблицы
/// маршрутов — это невозможно без прав администратора. Лаунчер сам НЕ повышает права и НЕ запускается
/// от администратора; повышенная часть изолирована и имеет минимальные полномочия.
///
/// Маршрутизация: помощник добавляет маршрут ТОЛЬКО на подсеть комнаты (например 10.64.0.0/24),
/// никогда не 0.0.0.0/0 — доступ к домашней сети и интернету не открывается.
/// </summary>
public sealed class WindowsWintunAdapter : INetworkAdapter
{
    private readonly IProcessRunner _runner;
    private readonly ILogService _log;
    private readonly HttpClient _helper;
    private readonly string _helperUrl;
    private string? _roomId;

    public WindowsWintunAdapter(INetworkPrivileges privileges, IProcessRunner runner, ILogService log)
    {
        _runner = runner;
        _log = log;
        _helperUrl = "http://127.0.0.1:38471";
        _helper = new HttpClient { Timeout = TimeSpan.FromSeconds(10), BaseAddress = new Uri(_helperUrl) };

        Availability = privileges.HasTunnelPrivileges
            ? TunAvailability.Available("Сетевой помощник EHOT запущен: туннель доступен.")
            : privileges.HasTunnelPrivileges == false && privileges.CanRequestElevation
                ? TunAvailability.Unavailable(
                    "Сетевой помощник не установлен или не запущен. Установка требует подтверждения UAC " +
                    "(Windows запросит права один раз). До установки доступны прямое соединение и relay.", requiresElevation: true)
                : TunAvailability.Unavailable("Сетевой помощник недоступен.");
    }

    public string Id => "windows-wintun";

    public TunAvailability Availability { get; }

    public event Action<TunnelStats>? StatsUpdated;

    public async Task<(string PublicKey, string PrivateKeyRef)> GenerateKeyPairAsync(CancellationToken ct = default)
    {
        if (!Availability.IsAvailable)
        {
            return (string.Empty, string.Empty);
        }

        var response = await _helper.GetFromJsonAsync<KeyPairResponse>("/v1/keys", ct).ConfigureAwait(false);
        return response is null ? (string.Empty, string.Empty) : (response.PublicKey, response.PrivateKeyRef);
    }

    public async Task<TunOperationResult> StartAsync(TunnelConfig config, CancellationToken ct = default)
    {
        if (!Availability.IsAvailable)
        {
            return TunOperationResult.Fail(Availability.Detail);
        }

        _roomId = Guid.NewGuid().ToString("N");

        var response = await _helper.PostAsJsonAsync("/v1/tunnel", new
        {
            roomId = _roomId,
            virtualIp = config.VirtualIp,
            subnet = config.Subnet,
            privateKeyRef = config.PrivateKeyRef,
            listenPort = config.ListenPort,
            peers = config.Peers.Select(p => new { p.PublicKey, p.AllowedIp, p.Endpoint })
        }, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.Warn("Network", $"Сетевой помощник отказал: {(int)response.StatusCode} {error}");
            return TunOperationResult.Fail($"Сетевой помощник вернул {(int)response.StatusCode}.");
        }

        _log.Info("Network", $"Туннель запущен: {config.VirtualIp} в {config.Subnet}, пиров {config.Peers.Count}");
        return TunOperationResult.Ok();
    }

    public async Task StopAsync()
    {
        if (_roomId is null || !Availability.IsAvailable)
        {
            return;
        }

        try
        {
            await _helper.DeleteAsync($"/v1/tunnel/{_roomId}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Network", "Не удалось остановить туннель: " + ex.Message);
        }
        finally
        {
            _roomId = null;
        }
    }

    public async Task<TunnelStats> GetStatsAsync(CancellationToken ct = default)
    {
        if (!Availability.IsAvailable)
        {
            return TunnelStats.Empty;
        }

        try
        {
            var stats = await _helper.GetFromJsonAsync<StatsResponse>("/v1/tunnel/stats", ct).ConfigureAwait(false);
            if (stats is null)
            {
                return TunnelStats.Empty;
            }

            var result = new TunnelStats(stats.BytesSent, stats.BytesReceived, stats.HandshakeAgeSeconds, DateTimeOffset.UtcNow);
            StatsUpdated?.Invoke(result);
            return result;
        }
        catch (Exception)
        {
            return TunnelStats.Empty;
        }
    }

    private sealed class KeyPairResponse
    {
        public string PublicKey { get; set; } = string.Empty;

        public string PrivateKeyRef { get; set; } = string.Empty;
    }

    private sealed class StatsResponse
    {
        public long BytesSent { get; set; }

        public long BytesReceived { get; set; }

        public int HandshakeAgeSeconds { get; set; }
    }
}
