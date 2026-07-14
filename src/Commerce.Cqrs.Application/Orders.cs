using Commerce.Cqrs.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Cqrs.Application;

public sealed record CreateOrder(IReadOnlyCollection<CreateOrderLine> Lines, string CustomerEmail) : ICommand<Guid>;
public sealed record CreateOrderLine(string Sku, string ProductName, int Quantity, decimal UnitPrice);
public sealed record ConfirmOrder(Guid OrderId) : ICommand<bool>;
public sealed record CancelOrder(Guid OrderId, string Reason) : ICommand<bool>;
public sealed record GetOrder(Guid OrderId) : IQuery<OrderDetails?>;
public sealed record SearchOrders(string? Status, int Page = 1, int PageSize = 20)
    : IQuery<PagedResult<OrderSummary>>;

public sealed class CreateOrderHandler(IOrderWriteRepository repository)
    : ICommandHandler<CreateOrder, Guid>
{
    public async Task<Guid> Handle(CreateOrder command, CancellationToken cancellationToken)
    {
        var lines = command.Lines.Select(line =>
            new OrderLine(line.Sku, line.ProductName, line.Quantity, line.UnitPrice));
        var order = Order.Create(command.CustomerEmail, lines);

        await repository.Add(order, cancellationToken);
        await repository.Save(cancellationToken);
        return order.Id;
    }
}

public sealed class ConfirmOrderHandler(IOrderWriteRepository repository)
    : ICommandHandler<ConfirmOrder, bool>
{
    public async Task<bool> Handle(ConfirmOrder command, CancellationToken cancellationToken)
    {
        var order = await repository.Get(command.OrderId, cancellationToken)
            ?? throw new NotFoundException(nameof(Order), command.OrderId);
        order.Confirm();
        await repository.Save(cancellationToken);
        return true;
    }
}

public sealed class CancelOrderHandler(IOrderWriteRepository repository)
    : ICommandHandler<CancelOrder, bool>
{
    public async Task<bool> Handle(CancelOrder command, CancellationToken cancellationToken)
    {
        var order = await repository.Get(command.OrderId, cancellationToken)
            ?? throw new NotFoundException(nameof(Order), command.OrderId);
        order.Cancel(command.Reason);
        await repository.Save(cancellationToken);
        return true;
    }
}

public sealed class GetOrderHandler(IOrderReadStore store) : IQueryHandler<GetOrder, OrderDetails?>
{
    public Task<OrderDetails?> Handle(GetOrder query, CancellationToken cancellationToken) =>
        store.Get(query.OrderId, cancellationToken);
}

public sealed class SearchOrdersHandler(IOrderReadStore store)
    : IQueryHandler<SearchOrders, PagedResult<OrderSummary>>
{
    public Task<PagedResult<OrderSummary>> Handle(SearchOrders query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        return store.Search(new OrderSearch(query.Status, page, pageSize), cancellationToken);
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<ICommandHandler<CreateOrder, Guid>, CreateOrderHandler>()
        .AddScoped<ICommandHandler<ConfirmOrder, bool>, ConfirmOrderHandler>()
        .AddScoped<ICommandHandler<CancelOrder, bool>, CancelOrderHandler>()
        .AddScoped<IQueryHandler<GetOrder, OrderDetails?>, GetOrderHandler>()
        .AddScoped<IQueryHandler<SearchOrders, PagedResult<OrderSummary>>, SearchOrdersHandler>();
}
