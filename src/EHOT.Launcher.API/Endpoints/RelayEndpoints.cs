using EHOT.Launcher.API.Data;
using Microsoft.EntityFrameworkCore;

namespace EHOT.Launcher.API.Endpoints;

public sealed record RelayDto(string Host, int Port, string Region, string PublicKey, int LoadPercent, long MonthlyQuotaBytes, long UsedBytesThisMonth);

public static class RelayEndpoints
{
    public static void MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/relays", async (ControlPlaneDbContext db, CancellationToken ct) =>
        {
            var relays = await db.RelayNodes
                .AsNoTracking()
                .Where(r => r.IsEnabled)
                .OrderBy(r => r.Region)
                .Select(r => new RelayDto(r.Host, r.Port, r.Region, r.PublicKey, r.LoadPercent, r.MonthlyQuotaBytes, r.UsedBytesThisMonth))
                .ToListAsync(ct);

            return Results.Ok(relays);
        })
        .RequireAuthorization()
        .WithTags("Relays")
        .WithName("GetRelays")
        .WithSummary("Список доступных relay-узлов. Квоты и загрузка реальные, а не декоративные.");

        app.MapGet("/v1/meta", () => Results.Ok(new
        {
            service = "EHOT Game Network Control Plane",
            version = "0.1.0",
            minLauncherVersion = "0.1.0",
            features = new[] { "rooms", "invites", "relays", "heartbeat" },
            notes = "Сервер не видит игровой трафик при прямом соединении; relay передаёт только зашифрованные пакеты."
        }))
        .AllowAnonymous()
        .WithTags("Meta")
        .WithName("GetMeta");
    }
}
