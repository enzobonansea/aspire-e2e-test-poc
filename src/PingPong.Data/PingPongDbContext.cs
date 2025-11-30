using Microsoft.EntityFrameworkCore;

namespace PingPong.Data;

public class PingPongDbContext : DbContext
{
    public PingPongDbContext(DbContextOptions<PingPongDbContext> options) : base(options)
    {
    }

    public DbSet<Ping> Pings => Set<Ping>();
    public DbSet<Pong> Pongs => Set<Pong>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SentAt).IsRequired();
        });

        modelBuilder.Entity<Pong>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PingId).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();
        });
    }
}
