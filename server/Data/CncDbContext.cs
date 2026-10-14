using CncDash.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncDash.Server.Data;

public class CncDbContext : DbContext
{
    public CncDbContext(DbContextOptions<CncDbContext> options) : base(options) { }

    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<MachineConfig> MachineConfigs => Set<MachineConfig>();
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<ToolAsset> ToolAssets => Set<ToolAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sample>(e =>
        {
            e.HasIndex(s => new { s.MachineId, s.TsUtc });
            e.HasIndex(s => new { s.MachineId, s.DataItemId, s.Sequence }).IsUnique();
        });

        modelBuilder.Entity<ToolAsset>(e =>
        {
            e.HasIndex(t => new { t.MachineId, t.AssetId }).IsUnique();
        });

        modelBuilder.Entity<MachineConfig>().HasKey(c => c.MachineId);
        modelBuilder.Entity<MachineConfig>()
            .HasOne(c => c.Machine)
            .WithOne(m => m.Config)
            .HasForeignKey<MachineConfig>(c => c.MachineId);
    }
}