using Commerce.Cqrs.Domain;

namespace Commerce.Cqrs.Application;

public interface ICommand<out TResult>;
public interface IQuery<out TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

public interface IOrderWriteRepository
{
    Task Add(Order order, CancellationToken cancellationToken);
    Task<Order?> Get(Guid id, CancellationToken cancellationToken);
    Task Save(CancellationToken cancellationToken);
}

public interface IOrderReadStore
{
    Task<OrderDetails?> Get(Guid id, CancellationToken cancellationToken);
    Task<PagedResult<OrderSummary>> Search(OrderSearch query, CancellationToken cancellationToken);
}

public sealed record OrderDetails(
    Guid Id,
    string CustomerEmail,
    string Status,
    decimal Total,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<OrderLineDetails> Lines);

public sealed record OrderLineDetails(string Sku, string ProductName, int Quantity, decimal UnitPrice);
public sealed record OrderSummary(Guid Id, string CustomerEmail, string Status, decimal Total, DateTimeOffset CreatedAtUtc);
public sealed record OrderSearch(string? Status, int Page = 1, int PageSize = 20);
public sealed record PagedResult<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int Total);

public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.");
