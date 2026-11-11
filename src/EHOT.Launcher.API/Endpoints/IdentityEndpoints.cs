using System.Security.Claims;
using EHOT.Launcher.API.Data;
using EHOT.Launcher.API.Services;
using Microsoft.EntityFrameworkCore;

namespace EHOT.Launcher.API.Endpoints;

public sealed record RegisterIdentityRequest(string PublicKey, string? DisplayName);

public sealed record RegisterIdentityResponse(Guid IdentityId, string Token, DateTimeOffset ExpiresAt);

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/identity").WithTags("Identity");

        group.MapPost("/register", async (
            RegisterIdentityRequest request,
            ControlPlaneDbContext db,
            TokenService tokens,
            ApiOptions options,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PublicKey) || request.PublicKey.Length is < 32 or > 128)
            {
                return Results.Problem("publicKey обязателен (32–128 символов, base64).", statusCode: 400);
            }

            var displayName = Sanitize(request.DisplayName) ?? "Игрок";

            var identity = await db.Identities.FirstOrDefaultAsync(i => i.PublicKey == request.PublicKey, ct);
            if (identity is null)
            {
                identity = new Identity
                {
                    PublicKey = request.PublicKey,
                    DisplayName = displayName
                };
                db.Identities.Add(identity);
            }
            else
            {
                if (identity.IsBlocked)
                {
                    return Results.Problem("Identity заблокирована.", statusCode: 403);
                }

                identity.LastSeenAt = DateTimeOffset.UtcNow;
                identity.DisplayName = displayName;
            }

            await db.SaveChangesAsync(ct);

            var token = tokens.CreateToken(identity.Id, identity.DisplayName);
            http.Response.Headers.CacheControl = "no-store";

            return Results.Ok(new RegisterIdentityResponse(
                identity.Id, token, DateTimeOffset.UtcNow.AddDays(options.TokenLifetimeDays)));
        })
        .WithName("RegisterIdentity")
        .WithSummary("Регистрирует анонимную identity лаунчера по публичному ключу Ed25519/Curve25519.")
        .AllowAnonymous();

        group.MapGet("/me", async (ClaimsPrincipal user, ControlPlaneDbContext db, CancellationToken ct) =>
        {
            var id = GetIdentityId(user);
            if (id is null)
            {
                return Results.Unauthorized();
            }

            var identity = await db.Identities.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            return identity is null
                ? Results.NotFound()
                : Results.Ok(new { identity.Id, identity.DisplayName, identity.CreatedAt, identity.LastSeenAt });
        })
        .RequireAuthorization()
        .WithName("GetIdentity");
    }

    internal static Guid? GetIdentityId(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    internal static string? Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        if (trimmed.Length > 32)
        {
            trimmed = trimmed[..32];
        }

        // Убираем управляющие символы и разметку — имя отображается в списке участников.
        return new string(trimmed.Where(c => !char.IsControl(c) && c is not '<' and not '>').ToArray());
    }
}

public sealed record CreateRoomRequest(string Name, int? MaxMembers, bool? AllowRelay);

public sealed record RoomInfoResponse(
    Guid RoomId,
    string JoinCode,
    string Name,
    string VirtualSubnet,
    Guid OwnerMemberId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaxMembers);

public sealed record PeerRegistrationDto(string DisplayName, string PublicKey, string? Endpoint);

public sealed record JoinRoomRequest(string JoinCode, PeerRegistrationDto Registration);

public sealed record PeerDto(
    string MemberId,
    string DisplayName,
    string VirtualIp,
    string PublicKey,
    string? Endpoint,
    string Transport,
    int LatencyMs,
    DateTimeOffset LastSeen,
    bool IsOwner);

public sealed record JoinRoomResponse(
    Guid RoomId,
    string Name,
    string VirtualSubnet,
    Guid OwnerMemberId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaxMembers,
    string MyMemberId,
    string MyVirtualIp,
    IReadOnlyList<PeerDto> Peers,
    int Revision,
    string Signature);
