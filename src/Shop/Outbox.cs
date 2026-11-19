using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shop;

public sealed class OutboxMessage
{
    private static readonly Dictionary<string, Type> EventTypes = typeof(IDomainEvent).Assembly.GetTypes()
        .Where(t => t.IsClass && typeof(IDomainEvent).IsAssignableFrom(t))
        .ToDictionary(t => t.Name);

    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? Error { get; set; }

    public static OutboxMessage From(IDomainEvent domainEvent, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Type = domainEvent.GetType().Name,
        Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
        OccurredAt = now
    };

    public IDomainEvent ToDomainEvent() =>
        (IDomainEvent)JsonSerializer.Deserialize(Payload, EventTypes[Type])!;
}

// Processes one message per transaction. FOR UPDATE SKIP LOCKED lets several
// app instances run the processor without picking the same message.
// A failed message waits (backoff) before the next attempt.
public sealed class OutboxProcessor(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<int> ProcessPending(CancellationToken cancellationToken, int maxMessages = 50)
    {
        var processed = 0;
        while (processed < maxMessages && await ProcessNext(cancellationToken))
        {
            processed++;
        }

        return processed;
    }

    private async Task<bool> ProcessNext(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var message = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND attempts < 5
                  AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY occurred_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (message is null)
        {
            return false;
        }

        try
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<DomainEventDispatcher>();
            await dispatcher.Dispatch([message.ToDomainEvent()], cancellationToken);

            // Handler writes and the processed mark commit together.
            message.ProcessedAt = now;
            message.Attempts++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await db.OutboxMessages
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1)
                    .SetProperty(m => m.NextAttemptAt, now.AddSeconds(30 * (message.Attempts + 1)))
                    .SetProperty(m => m.Error, ex.Message), cancellationToken);
        }

        return true;
    }
}

public sealed class OutboxBackgroundService(OutboxProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            await processor.ProcessPending(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
