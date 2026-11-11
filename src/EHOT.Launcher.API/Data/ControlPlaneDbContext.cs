using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EHOT.Launcher.API.Data;

public class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    public DbSet<Identity> Identities => Set<Identity>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<RelayNode> RelayNodes => Set<RelayNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite не умеет сравнивать DateTimeOffset в SQL (значения хранятся как TEXT и не упорядочиваются).
        // Для окружения Testing/локальной разработки переводим их в тики UTC, чтобы запросы с TTL
        // (истечение комнат и инвайтов) выполнялись на стороне БД, а не падали в 500.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(new DateTimeOffsetToTicksConverter());
                    }
                }
            }
        }

        modelBuilder.Entity<Identity>(e =>
        {
            e.ToTable("identities");
            e.HasKey(x => x.Id);
            e.Property(x => x.PublicKey).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.PublicKey).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(32);
        });

        modelBuilder.Entity<Room>(e =>
        {
            e.ToTable("rooms");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(48).IsRequired();
            e.Property(x => x.JoinCode).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.JoinCode);
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.HasMany(x => x.Members).WithOne(x => x.Room!).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Invites).WithOne(x => x.Room!).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoomMember>(e =>
        {
            e.ToTable("room_members");
            e.HasKey(x => x.Id);
            e.Property(x => x.VirtualIp).HasMaxLength(45).IsRequired();
            e.Property(x => x.WireGuardPublicKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(64);
            e.HasIndex(x => new { x.RoomId, x.IdentityId }).IsUnique();
            e.HasIndex(x => new { x.RoomId, x.VirtualIp }).IsUnique();
        });

        modelBuilder.Entity<Invite>(e =>
        {
            e.ToTable("invites");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.TokenHash);
        });

        modelBuilder.Entity<RelayNode>(e =>
        {
            e.ToTable("relay_nodes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Host).HasMaxLength(128).IsRequired();
            e.Property(x => x.Region).HasMaxLength(16);
        });
    }
}

/// <summary>
/// DateTimeOffset ⇄ тики UTC: для SQLite это даёт корректные сравнения в SQL
/// (Production использует PostgreSQL, где тип `timestamptz` поддерживается нативно).
/// </summary>
internal sealed class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
