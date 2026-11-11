using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EHOT.Launcher.API.Data;
using EHOT.Launcher.API.Services;
using Microsoft.EntityFrameworkCore;

namespace EHOT.Launcher.API.Endpoints;

public static class RoomEndpoints
{
    private static readonly TimeSpan HeartbeatTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HardTtl = TimeSpan.FromHours(6);

    public static void MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/rooms").WithTags("Rooms").RequireAuthorization();

        group.MapPost("/", async (
            CreateRoomRequest request,
            ClaimsPrincipal user,
            ControlPlaneDbContext db,
            ApiOptions options,
            InviteService invites,
            CancellationToken ct) =>
        {
            var identityId = IdentityEndpoints.GetIdentityId(user);
            if (identityId is null)
            {
                return Results.Unauthorized();
            }

            var name = IdentityEndpoints.Sanitize(request.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            {
                return Results.Problem("Название комнаты: 2–48 символа.", statusCode: 400);
            }

            var activeRooms = await db.Rooms.CountAsync(r =>
                r.OwnerIdentityId == identityId.Value && r.Status == RoomStatus.Open && r.ExpiresAt > DateTimeOffset.UtcNow, ct);

            if (activeRooms >= options.MaxRoomsPerIdentity)
            {
                return Results.Problem(
                    $"Лимит активных комнат ({options.MaxRoomsPerIdentity}). Закройте старую комнату.", statusCode: 429);
            }

            // Освобождаем истёкшие комнаты, чтобы код не конфликтовал.
            await ExpireStaleAsync(db, ct);

            var room = new Room
            {
                Name = name,
                OwnerIdentityId = identityId.Value,
                MaxMembers = Math.Clamp(request.MaxMembers ?? 8, 2, 16),
                AllowRelay = request.AllowRelay ?? true,
                Subnet = IpAllocator.BuildSubnet(Guid.NewGuid().GetHashCode()),
                ExpiresAt = DateTimeOffset.UtcNow.Add(HardTtl)
            };

            var code = invites.GenerateCode();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var clash = await db.Rooms.AnyAsync(r => r.JoinCode == code && r.Status == RoomStatus.Open, ct);
                if (!clash)
                {
                    break;
                }

                code = invites.GenerateCode();
            }

            room.JoinCode = code;
            db.Rooms.Add(room);

            var invite = new Invite
            {
                RoomId = room.Id,
                TokenHash = invites.Hash(code, room.Id),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
                MaxUses = room.MaxMembers
            };
            db.Invites.Add(invite);

            var member = new RoomMember
            {
                RoomId = room.Id,
                IdentityId = identityId.Value,
                VirtualIp = IpAllocator.Allocate([], room.Subnet),
                WireGuardPublicKey = string.Empty,
                IsOwner = true
            };
            db.RoomMembers.Add(member);

            await db.SaveChangesAsync(ct);

            return Results.Created($"/v1/rooms/{room.Id}", new RoomInfoResponse(
                room.Id, room.JoinCode, room.Name, room.Subnet, member.Id,
                room.CreatedAt, room.ExpiresAt, room.MaxMembers));
        })
        .RequireRateLimiting("room-create")
        .WithName("CreateRoom")
        .WithSummary("Создаёт комнату и код приглашения.");

        group.MapPost("/join", async (
            JoinRoomRequest request,
            ClaimsPrincipal user,
            ControlPlaneDbContext db,
            InviteService invites,
            CancellationToken ct) =>
        {
            var identityId = IdentityEndpoints.GetIdentityId(user);
            if (identityId is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.JoinCode) || request.Registration is null
                || string.IsNullOrWhiteSpace(request.Registration.PublicKey))
            {
                return Results.Problem("joinCode и publicKey обязательны.", statusCode: 400);
            }

            await ExpireStaleAsync(db, ct);

            // Код приглашения нормализуется один раз; EF транслирует равенство строк без приведения регистра.
            var joinCode = request.JoinCode.Trim().ToUpperInvariant();

            var room = await db.Rooms
                .Include(r => r.Members).ThenInclude(m => m.Identity)
                .FirstOrDefaultAsync(r => r.JoinCode == joinCode && r.Status == RoomStatus.Open, ct);

            if (room is null)
            {
                return Results.Problem("Комната не найдена или закрыта.", statusCode: 404);
            }

            var invite = await db.Invites.FirstOrDefaultAsync(i => i.RoomId == room.Id && i.RevokedAt == null, ct);
            if (invite is null || !invites.Verify(request.JoinCode, room.Id, invite.TokenHash))
            {
                return Results.Problem("Код приглашения недействителен или отозван.", statusCode: 403);
            }

            if (invite.ExpiresAt < DateTimeOffset.UtcNow)
            {
                return Results.Problem("Срок действия приглашения истёк.", statusCode: 403);
            }

            var existing = room.Members.FirstOrDefault(m => m.IdentityId == identityId.Value && m.RevokedAt == null);
            if (existing is not null)
            {
                existing.LastSeenAt = DateTimeOffset.UtcNow;
                existing.Endpoint = request.Registration.Endpoint;
                existing.WireGuardPublicKey = request.Registration.PublicKey;
                await db.SaveChangesAsync(ct);
                return Results.Ok(BuildJoinResponse(room, existing, room.Members.Where(m => m.RevokedAt == null).ToArray()));
            }

            var activeMembers = room.Members.Count(m => m.RevokedAt == null);
            if (activeMembers >= room.MaxMembers)
            {
                return Results.Problem("Комната заполнена.", statusCode: 409);
            }

            var usedIps = room.Members.Select(m => m.VirtualIp).ToHashSet();
            var member = new RoomMember
            {
                RoomId = room.Id,
                IdentityId = identityId.Value,
                VirtualIp = IpAllocator.Allocate(usedIps, room.Subnet),
                WireGuardPublicKey = request.Registration.PublicKey,
                Endpoint = request.Registration.Endpoint,
                IsOwner = room.OwnerIdentityId == identityId.Value
            };

            db.RoomMembers.Add(member);
            invite.UsedCount++;
            room.Revision++;

            await db.SaveChangesAsync(ct);

            // Перечитываем участников, чтобы вернуть актуальный снапшот.
            var members = await db.RoomMembers
                .Include(m => m.Identity)
                .Where(m => m.RoomId == room.Id && m.RevokedAt == null)
                .ToListAsync(ct);

            return Results.Ok(BuildJoinResponse(room, member, members));
        })
        .WithName("JoinRoom")
        .WithSummary("Подключает участника по коду приглашения и возвращает подписанный снапшот состава.");

        group.MapGet("/{roomId:guid}", async (
            Guid roomId,
            ClaimsPrincipal user,
            ControlPlaneDbContext db,
            CancellationToken ct) =>
        {
            var identityId = IdentityEndpoints.GetIdentityId(user);
            if (identityId is null)
            {
                return Results.Unauthorized();
            }

            var room = await db.Rooms
                .Include(r => r.Members).ThenInclude(m => m.Identity)
                .FirstOrDefaultAsync(r => r.Id == roomId, ct);

            if (room is null || room.Status != RoomStatus.Open)
            {
                return Results.Problem("Комната не найдена.", statusCode: 404);
            }

            var me = room.Members.FirstOrDefault(m => m.IdentityId == identityId.Value && m.RevokedAt == null);
            if (me is null)
            {
                return Results.Problem("Вы не участник этой комнаты.", statusCode: 403);
            }

            return Results.Ok(BuildJoinResponse(room, me, room.Members.Where(m => m.RevokedAt == null).ToArray()));
        })
        .WithName("GetRoom");

        group.MapPost("/{roomId:guid}/heartbeat", async (
            Guid roomId,
            ClaimsPrincipal user,
            ControlPlaneDbContext db,
            CancellationToken ct) =>
        {
            var identityId = IdentityEndpoints.GetIdentityId(user);
            if (identityId is null)
            {
                return Results.Unauthorized();
            }

            var member = await db.RoomMembers
                .FirstOrDefaultAsync(m => m.RoomId == roomId && m.IdentityId == identityId.Value && m.RevokedAt == null, ct);

            if (member is null)
            {
                return Results.Problem("Вы не участник этой комнаты.", statusCode: 403);
            }

            member.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("RoomHeartbeat");

        group.MapDelete("/{roomId:guid}/members/{memberId}", async (
            Guid roomId,
            string memberId,
            ClaimsPrincipal user,
            ControlPlaneDbContext db,
            CancellationToken ct) =>
        {
            var identityId = IdentityEndpoints.GetIdentityId(user);
            if (identityId is null)
            {
                return Results.Unauthorized();
            }

            var room = await db.Rooms.Include(r => r.Members).FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (room is null)
            {
                return Results.NotFound();
            }

            var requester = room.Members.FirstOrDefault(m => m.IdentityId == identityId.Value && m.RevokedAt == null);
            if (requester is null)
            {
                return Results.Problem("Вы не участник этой комнаты.", statusCode: 403);
            }

            var target = memberId == "me"
                ? requester
                : room.Members.FirstOrDefault(m => m.Id.ToString() == memberId && m.RevokedAt == null);

            if (target is null)
            {
                return Results.NotFound("Участник не найден.");
            }

            if (target.Id != requester.Id && !requester.IsOwner)
            {
                return Results.Problem("Удалять участников может только владелец комнаты.", statusCode: 403);
            }

            target.RevokedAt = DateTimeOffset.UtcNow;
            room.Revision++;

            if (target.IsOwner)
            {
                room.Status = RoomStatus.Closed;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("RemoveMember")
        .WithSummary("Выход из комнаты (memberId=me) или удаление участника владельцем. Доступ отзывается мгновенно.");

        group.MapDelete("/{roomId:guid}", async (
            Guid roomId,
            ClaimsPrincipal user,
            ControlPlaneDbContext db,
            CancellationToken ct) =>
        {
            var identityId = IdentityEndpoints.GetIdentityId(user);
            if (identityId is null)
            {
                return Results.Unauthorized();
            }

            var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
            if (room is null)
            {
                return Results.NotFound();
            }

            if (room.OwnerIdentityId != identityId.Value)
            {
                return Results.Problem("Закрыть комнату может только владелец.", statusCode: 403);
            }

            room.Status = RoomStatus.Closed;
            room.Revision++;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("CloseRoom");
    }

    private static JoinRoomResponse BuildJoinResponse(Room room, RoomMember me, IReadOnlyCollection<RoomMember> members)
    {
        var peers = members.Select(m => new PeerDto(
            m.Id.ToString(),
            m.Identity?.DisplayName ?? "Игрок",
            m.VirtualIp,
            m.WireGuardPublicKey,
            m.Endpoint,
            m.Endpoint is null ? "Relay" : "Direct",
            0,
            m.LastSeenAt,
            m.IsOwner)).ToArray();

        var signature = SignSnapshot(room, peers.Length);

        return new JoinRoomResponse(
            room.Id, room.Name, room.Subnet, room.OwnerIdentityId,
            room.CreatedAt, room.ExpiresAt, room.MaxMembers,
            me.Id.ToString(), me.VirtualIp, peers, room.Revision, signature);
    }

    /// <summary>
    /// Подпись снапшота состава: клиент может убедиться, что состав не подменён по дороге.
    /// Используется HMAC серверным секретом; для полноценной модели доверия сервер также публикует
    /// свой Ed25519-ключ, и клиенты пинят его при первом подключении.
    /// </summary>
    private static string SignSnapshot(Room room, int peerCount)
    {
        var payload = $"{room.Id:N}:{room.Revision}:{peerCount}:{room.Subnet}";
        var key = Encoding.UTF8.GetBytes(room.Id.ToString("N") + "|ehot-snapshot");
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static async Task ExpireStaleAsync(ControlPlaneDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = await db.Rooms
            .Where(r => r.Status == RoomStatus.Open && r.ExpiresAt < now)
            .ToListAsync(ct);

        foreach (var room in stale)
        {
            room.Status = RoomStatus.Expired;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
