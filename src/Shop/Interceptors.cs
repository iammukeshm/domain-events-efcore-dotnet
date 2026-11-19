using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Shop;

internal static class DomainEventCollector
{
    public static List<IDomainEvent> Collect(DbContext context)
    {
        var entities = context.ChangeTracker.Entries<Entity>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();

        var events = entities.SelectMany(e => e.DomainEvents).ToList();
        entities.ForEach(e => e.ClearDomainEvents());
        return events;
    }
}

// Option A: dispatch right BEFORE the commit, inside the same SaveChanges call.
public sealed class BeforeCommitDispatchInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var events = DomainEventCollector.Collect(eventData.Context!);
        await services.GetRequiredService<DomainEventDispatcher>().Dispatch(events, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Use SaveChangesAsync.");
}

// Option B: dispatch right AFTER SaveChanges has saved the data.
public sealed class AfterCommitDispatchInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private List<IDomainEvent> _pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        _pending = DomainEventCollector.Collect(eventData.Context!);
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var events = _pending;
        _pending = [];
        if (events.Count == 0)
        {
            return result;
        }

        await services.GetRequiredService<DomainEventDispatcher>().Dispatch(events, cancellationToken);

        // Handlers that wrote to the database need a second save: a second transaction.
        if (eventData.Context!.ChangeTracker.HasChanges())
        {
            await eventData.Context.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Use SaveChangesAsync.");
}

// Option C: write the events to an outbox table in the same transaction.
public sealed class OutboxInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context!;
        foreach (var domainEvent in DomainEventCollector.Collect(context))
        {
            context.Add(OutboxMessage.From(domainEvent, clock.GetUtcNow()));
        }

        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Use SaveChangesAsync.");
}
