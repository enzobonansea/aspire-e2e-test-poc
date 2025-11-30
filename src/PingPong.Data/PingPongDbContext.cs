using Microsoft.EntityFrameworkCore;

namespace PingPong.Data;

public class PingPongDbContext : DbContext
{
    public PingPongDbContext(DbContextOptions<PingPongDbContext> options) : base(options)
    {
    }

    public DbSet<Ping> Pings => Set<Ping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SentAt).IsRequired();
        });
    }
}
