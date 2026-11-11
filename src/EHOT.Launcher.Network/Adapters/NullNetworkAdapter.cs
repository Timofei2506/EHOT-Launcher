using EHOT.Launcher.Core.Diagnostics;

namespace EHOT.Launcher.Network.Adapters;

/// <summary>
/// Адаптер «без туннеля»: используется, когда платформенный TUN недоступен (Linux, тесты, macOS без entitlement).
/// Честно сообщает об этом; Game Network в таком режиме ограничивается прямым соединением и relay.
/// </summary>
public sealed class NullNetworkAdapter(ILogService log) : INetworkAdapter
{
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;

    public string Id => "null";

    public TunAvailability Availability { get; } = TunAvailability.Unavailable(
        "Системный туннель недоступен на этой платформе: доступны прямое соединение и relay-режим. " +
        "Полноценный LAN-туннель требует привилегированного сетевого помощника.");

    // Null-адаптер не отдаёт статистику: туннеля нет, событие нужно только для контракта.
#pragma warning disable CS0067
    public event Action<TunnelStats>? StatsUpdated;
#pragma warning restore CS0067

    public Task<(string PublicKey, string PrivateKeyRef)> GenerateKeyPairAsync(CancellationToken ct = default)
    {
        // Без туннеля ключи не нужны, но контракт должен возвращать стабильное значение.
        return Task.FromResult(("", string.Empty));
    }

    public Task<TunOperationResult> StartAsync(TunnelConfig config, CancellationToken ct = default)
    {
        log.Warn("Network", "Запрошен старт туннеля, но адаптер недоступен: " + Availability.Detail);
        return Task.FromResult(TunOperationResult.Fail(Availability.Detail));
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task<TunnelStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(TunnelStats.Empty);
}
