namespace Shop;

public interface IDomainEvent;

public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

public sealed record OrderPlaced(Guid OrderId, string Sku, int Quantity, string CustomerEmail) : IDomainEvent;

public sealed class Order : Entity
{
    private Order() { }

    public Guid Id { get; private set; }
    public string Number { get; private set; } = "";
    public string Sku { get; private set; } = "";
    public int Quantity { get; private set; }
    public string CustomerEmail { get; private set; } = "";

    public static Order Place(string number, string sku, int quantity, string customerEmail)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            Number = number,
            Sku = sku,
            Quantity = quantity,
            CustomerEmail = customerEmail
        };

        order.Raise(new OrderPlaced(order.Id, sku, quantity, customerEmail));
        return order;
    }
}

public sealed class StockReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}
