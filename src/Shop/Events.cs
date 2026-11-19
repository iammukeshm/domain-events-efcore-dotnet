namespace Shop;

public interface IDomainEventHandler
{
    Type EventType { get; }
    Task Handle(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

public abstract class DomainEventHandler<TEvent> : IDomainEventHandler where TEvent : IDomainEvent
{
    public Type EventType => typeof(TEvent);

    public Task Handle(IDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Handle((TEvent)domainEvent, cancellationToken);

    protected abstract Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}

// A plain dispatcher: no mediator library, handlers come from DI.
public sealed class DomainEventDispatcher(IEnumerable<IDomainEventHandler> handlers)
{
    public async Task Dispatch(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in domainEvents)
        {
            foreach (var handler in handlers.Where(h => h.EventType == domainEvent.GetType()))
            {
                await handler.Handle(domainEvent, cancellationToken);
            }
        }
    }
}

// Writes to the database through the same DbContext.
public sealed class ReserveStockHandler(ShopDbContext db) : DomainEventHandler<OrderPlaced>
{
    protected override Task Handle(OrderPlaced e, CancellationToken cancellationToken)
    {
        db.StockReservations.Add(new StockReservation { OrderId = e.OrderId, Sku = e.Sku, Quantity = e.Quantity });
        return Task.CompletedTask;
    }
}

// Talks to the outside world. The database cannot roll this back.
public interface IEmailSender
{
    Task Send(string to, string subject, CancellationToken cancellationToken);
}

public sealed class SendConfirmationEmailHandler(IEmailSender email) : DomainEventHandler<OrderPlaced>
{
    protected override Task Handle(OrderPlaced e, CancellationToken cancellationToken) =>
        email.Send(e.CustomerEmail, $"Order {e.OrderId} confirmed", cancellationToken);
}
