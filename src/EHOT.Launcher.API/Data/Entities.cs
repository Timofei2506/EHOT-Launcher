namespace EHOT.Launcher.API.Data;

/// <summary>Идентичность лаунчера: Ed25519-ключ, без e-mail и персональных данных.</summary>
public class Identity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string PublicKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = "Игрок";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsBlocked { get; set; }

    public ICollection<RoomMember> Memberships { get; set; } = new List<RoomMember>();
}

public enum RoomStatus
{
    Open = 0,
    Closed = 1,
    Expired = 2
}

public class Room
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Код приглашения (base32, 8 символов). Уникален среди активных комнат.</summary>
    public string JoinCode { get; set; } = string.Empty;

    public string Subnet { get; set; } = "10.64.0.0/24";

    public Guid OwnerIdentityId { get; set; }

    public RoomStatus Status { get; set; } = RoomStatus.Open;

    public int MaxMembers { get; set; } = 8;

    public bool AllowRelay { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Комната автоматически закрывается, если владелец не продлевает её heartbeat'ом.</summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(6);

    public int Revision { get; set; } = 1;

    public ICollection<RoomMember> Members { get; set; } = new List<RoomMember>();

    public ICollection<Invite> Invites { get; set; } = new List<Invite>();
}

public class RoomMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }

    public Room? Room { get; set; }

    public Guid IdentityId { get; set; }

    public Identity? Identity { get; set; }

    public string VirtualIp { get; set; } = string.Empty;

    public string WireGuardPublicKey { get; set; } = string.Empty;

    public string? Endpoint { get; set; }

    public bool IsOwner { get; set; }

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }
}

public class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }

    public Room? Room { get; set; }

    /// <summary>HMAC-SHA256 подпись; сам код не хранится в открытом виде.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);

    public DateTimeOffset? RevokedAt { get; set; }

    public int UsedCount { get; set; }

    public int MaxUses { get; set; } = 8;
}

public class RelayNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 443;

    public string Region { get; set; } = "eu";

    public string PublicKey { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public int LoadPercent { get; set; }

    public long MonthlyQuotaBytes { get; set; }

    public long UsedBytesThisMonth { get; set; }
}
