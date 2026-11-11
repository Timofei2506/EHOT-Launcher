using Microsoft.EntityFrameworkCore;

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
