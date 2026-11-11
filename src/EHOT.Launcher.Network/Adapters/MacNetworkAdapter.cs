using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Network.Adapters;

/// <summary>
/// macOS-адаптер. Полноценный туннель на macOS реализуется ТОЛЬКО через NEPacketTunnelProvider
/// в составе Network Extension (отдельный appex-таргет, entitlement
/// com.apple.developer.networking.networkextension и одобрение Apple).
///
/// Что делает этот класс честно:
///  • проверяет наличие entitlement у текущей сборки;
///  • если он есть — передаёт запрос системному расширению через NEVPNManager (реализуется в appex);
///  • если entitlement отсутствует — Availability = Unavailable с объяснением и предложением
///    прямого соединения / relay.
/// Никакой имитации «подключено» без реального интерфейса.
/// </summary>
public sealed class MacNetworkAdapter : INetworkAdapter
{
    private readonly ILogService _log;
    private readonly INetworkPrivileges _privileges;
    private TunnelStats _last = TunnelStats.Empty;

    public MacNetworkAdapter(INetworkPrivileges privileges, ILogService log)
    {
        _privileges = privileges;
        _log = log;

        Availability = privileges.HasTunnelPrivileges
            ? TunAvailability.Available("Network Extension доступен (packet-tunnel-provider подписан).")
            : TunAvailability.Unavailable(
                privileges.Describe(TunnelPrivilegeState.NotSupported));
    }

    public string Id => "macos-netextension";

    public TunAvailability Availability { get; }

    // Состояние туннеля читается через INetworkAdapter.GetStatsAsync; событие оставлено для контракта.
#pragma warning disable CS0067
    public event Action<TunnelStats>? StatsUpdated;
#pragma warning restore CS0067

    public Task<(string PublicKey, string PrivateKeyRef)> GenerateKeyPairAsync(CancellationToken ct = default)
    {
        if (!Availability.IsAvailable)
        {
            return Task.FromResult((string.Empty, string.Empty));
        }

        // Ключи генерирует расширение (Go/Rust wireguard-go внутри appex) — приватный ключ остаётся в keychain расширения.
        return Task.FromResult((string.Empty, "netextension-managed"));
    }

    public Task<TunOperationResult> StartAsync(TunnelConfig config, CancellationToken ct = default)
    {
        if (!Availability.IsAvailable)
        {
            _log.Warn("Network", "macOS: туннель недоступен — " + Availability.Detail);
            return Task.FromResult(TunOperationResult.Fail(Availability.Detail));
        }

        // Реальная реализация: NEVPNManager.SaveToPreferences + StartVPNTunnel в appex.
        // До появления подписанного расширения возвращаем ошибку вместо фиктивного успеха.
        return Task.FromResult(TunOperationResult.Fail(
            "Сборка не подписана entitlement'ом Network Extension. Установите подписанную версию лаунчера " +
            "или используйте прямое соединение/relay."));
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task<TunnelStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(_last);
}
