using Microsoft.EntityFrameworkCore;

namespace Shop;

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(order =>
        {
            order.Ignore(o => o.DomainEvents);
            order.HasIndex(o => o.Number).IsUnique();
        });

        modelBuilder.Entity<StockReservation>().HasIndex(r => r.OrderId).IsUnique();

        modelBuilder.Entity<OutboxMessage>(message =>
        {
            message.ToTable("outbox_messages");
            message.Property(m => m.Id).HasColumnName("id");
            message.Property(m => m.Type).HasColumnName("type");
            message.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            message.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            message.Property(m => m.ProcessedAt).HasColumnName("processed_at");
            message.Property(m => m.Attempts).HasColumnName("attempts");
            message.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
            message.Property(m => m.Error).HasColumnName("error");
            message.HasIndex(m => m.OccurredAt).HasFilter("processed_at IS NULL");
        });
    }
}
