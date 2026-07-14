namespace Commerce.Cqrs.Domain;

public sealed class Order
{
    private readonly List<OrderLine> _lines = [];
    private readonly List<DomainEvent> _domainEvents = [];

    private Order() { }

    private Order(Guid id, string customerEmail, IEnumerable<OrderLine> lines)
    {
        Id = id;
        CustomerEmail = string.IsNullOrWhiteSpace(customerEmail)
            ? throw new DomainException("Customer email is required.")
            : customerEmail.Trim().ToLowerInvariant();
        _lines.AddRange(lines);

        if (_lines.Count == 0)
            throw new DomainException("An order must contain at least one line.");

        Status = OrderStatus.Draft;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        _domainEvents.Add(new OrderCreated(Id, CustomerEmail, Total, CreatedAtUtc));
    }

    public Guid Id { get; private set; }
    public string CustomerEmail { get; private set; } = string.Empty;
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public IReadOnlyCollection<OrderLine> Lines => _lines;
    public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents;
    public decimal Total => _lines.Sum(line => line.Quantity * line.UnitPrice);

    public static Order Create(string customerEmail, IEnumerable<OrderLine> lines) =>
        new(Guid.NewGuid(), customerEmail, lines);

    public void Confirm()
    {
        if (Status != OrderStatus.Draft)
            throw new DomainException("Only draft orders can be confirmed.");

        Status = OrderStatus.Confirmed;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        _domainEvents.Add(new OrderConfirmed(Id, Total, UpdatedAtUtc.Value));
    }

    public void Cancel(string reason)
    {
        if (Status == OrderStatus.Cancelled)
            throw new DomainException("The order is already cancelled.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A cancellation reason is required.");

        Status = OrderStatus.Cancelled;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        _domainEvents.Add(new OrderCancelled(Id, reason.Trim(), UpdatedAtUtc.Value));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}

public sealed class OrderLine
{
    private OrderLine() { }

    public OrderLine(string sku, string productName, int quantity, decimal unitPrice)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new DomainException("SKU is required.");
        if (string.IsNullOrWhiteSpace(productName)) throw new DomainException("Product name is required.");
        if (quantity <= 0) throw new DomainException("Quantity must be positive.");
        if (unitPrice <= 0) throw new DomainException("Unit price must be positive.");

        Id = Guid.NewGuid();
        Sku = sku.Trim().ToUpperInvariant();
        ProductName = productName.Trim();
        Quantity = quantity;
        UnitPrice = decimal.Round(unitPrice, 2);
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
}

public enum OrderStatus { Draft, Confirmed, Cancelled }

public abstract record DomainEvent(Guid AggregateId, DateTimeOffset OccurredAtUtc);
public sealed record OrderCreated(Guid OrderId, string CustomerEmail, decimal Total, DateTimeOffset OccurredAtUtc)
    : DomainEvent(OrderId, OccurredAtUtc);
public sealed record OrderConfirmed(Guid OrderId, decimal Total, DateTimeOffset OccurredAtUtc)
    : DomainEvent(OrderId, OccurredAtUtc);
public sealed record OrderCancelled(Guid OrderId, string Reason, DateTimeOffset OccurredAtUtc)
    : DomainEvent(OrderId, OccurredAtUtc);

public sealed class DomainException(string message) : Exception(message);
