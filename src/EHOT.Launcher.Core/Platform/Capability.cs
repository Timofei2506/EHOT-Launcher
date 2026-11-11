namespace EHOT.Launcher.Core.Platform;

/// <summary>Статус возможности подсистемы — честный, без «зелёных плашек по умолчанию».</summary>
public enum CapabilityState
{
    /// <summary>Работает и проверено на текущей платформе.</summary>
    Available = 0,

    /// <summary>Работает, но с известными ограничениями (см. <see cref="Capability.Detail"/>).</summary>
    Degraded = 1,

    /// <summary>Функция недоступна; причина обязательна к отображению пользователю.</summary>
    Unavailable = 2
}

/// <summary>Описание одной возможности, которое можно показать пользователю.</summary>
public sealed record Capability(string Id, string Title, CapabilityState State, string? Detail = null)
{
    public static Capability Available(string id, string title, string? detail = null)
        => new(id, title, CapabilityState.Available, detail);

    public static Capability Degraded(string id, string title, string detail)
        => new(id, title, CapabilityState.Degraded, detail);

    public static Capability Unavailable(string id, string title, string detail)
        => new(id, title, CapabilityState.Unavailable, detail);
}

/// <summary>Реестр возможностей на текущей машине. Потокобезопасен.</summary>
public interface ICapabilityRegistry
{
    IReadOnlyCollection<Capability> All { get; }

    void Set(Capability capability);

    Capability Get(string id);
}

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, Capability> _map = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyCollection<Capability> All
    {
        get
        {
            lock (_gate)
            {
                return _map.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
            }
        }
    }

    public void Set(Capability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_gate)
        {
            _map[capability.Id] = capability;
        }
    }

    public Capability Get(string id)
    {
        lock (_gate)
        {
            return _map.TryGetValue(id, out var c)
                ? c
                : Capability.Unavailable(id, id, "Возможность не инициализирована.");
        }
    }
}

/// <summary>Известные идентификаторы возможностей (используются в UI и тестах).</summary>
public static class CapabilityIds
{
    public const string MicrosoftAuth = "auth.microsoft";
    public const string LocalAuth = "auth.local";
    public const string GameLaunch = "game.launch";
    public const string JavaAutoDownload = "java.autodownload";
    public const string ContentModrinth = "content.modrinth";
    public const string ContentCurseForge = "content.curseforge";
    public const string GameNetworkControl = "network.control";
    public const string GameNetworkTunnel = "network.tunnel";
    public const string GameNetworkRelay = "network.relay";
    public const string Notifications = "platform.notifications";
    public const string AutoUpdate = "platform.autoupdate";
}
