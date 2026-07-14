using System.Data;
using System.Text.Json;
using Commerce.Cqrs.Application;
using Commerce.Cqrs.Domain;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Cqrs.Infrastructure;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Type).HasMaxLength(250).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
            entity.HasIndex(message => new { message.ProcessedAtUtc, message.OccurredAtUtc });
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var aggregates = ChangeTracker.Entries<Order>()
            .Select(entry => entry.Entity)
            .Where(order => order.DomainEvents.Count > 0)
            .ToArray();

        foreach (var domainEvent in aggregates.SelectMany(order => order.DomainEvents))
        {
            OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                AggregateId = domainEvent.AggregateId,
                OccurredAtUtc = domainEvent.OccurredAtUtc,
                Type = domainEvent.GetType().Name,
                Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType())
            });
        }

        var result = await base.SaveChangesAsync(cancellationToken);
        foreach (var aggregate in aggregates) aggregate.ClearDomainEvents();
        return result;
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> entity)
    {
        entity.ToTable("Orders");
        entity.HasKey(order => order.Id);
        entity.Property(order => order.CustomerEmail).HasMaxLength(320).IsRequired();
        entity.Property(order => order.Status).HasConversion<string>().HasMaxLength(30);
        entity.Property(order => order.CreatedAtUtc).HasPrecision(3);
        entity.Property(order => order.UpdatedAtUtc).HasPrecision(3);
        entity.Ignore(order => order.Total);
        entity.Ignore(order => order.DomainEvents);

        entity.OwnsMany(order => order.Lines, lines =>
        {
            lines.ToTable("OrderLines");
            lines.WithOwner().HasForeignKey(line => line.OrderId);
            lines.HasKey(line => line.Id);
            lines.Property(line => line.Sku).HasMaxLength(64).IsRequired();
            lines.Property(line => line.ProductName).HasMaxLength(200).IsRequired();
            lines.Property(line => line.UnitPrice).HasPrecision(18, 2);
        });
    }
}

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public Guid AggregateId { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? Error { get; set; }
}

internal sealed class OrderWriteRepository(OrdersDbContext dbContext) : IOrderWriteRepository
{
    public Task Add(Order order, CancellationToken cancellationToken) =>
        dbContext.Orders.AddAsync(order, cancellationToken).AsTask();

    public Task<Order?> Get(Guid id, CancellationToken cancellationToken) =>
        dbContext.Orders.Include(order => order.Lines)
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);

    public Task Save(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

internal sealed class SqlOrderReadStore(string connectionString) : IOrderReadStore
{
    private IDbConnection CreateConnection() => new SqlConnection(connectionString);

    public async Task<OrderDetails?> Get(Guid id, CancellationToken cancellationToken)
    {
        const string orderSql = """
            SELECT Id, CustomerEmail, Status, CreatedAtUtc,
                   (SELECT SUM(Quantity * UnitPrice) FROM OrderLines WHERE OrderId = o.Id) AS Total
            FROM Orders o WHERE Id = @Id;
            """;
        const string linesSql = """
            SELECT Sku, ProductName, Quantity, UnitPrice
            FROM OrderLines WHERE OrderId = @Id ORDER BY ProductName;
            """;

        using var connection = CreateConnection();
        var order = await connection.QuerySingleOrDefaultAsync<OrderRow>(
            new CommandDefinition(orderSql, new { Id = id }, cancellationToken: cancellationToken));
        if (order is null) return null;

        var lines = await connection.QueryAsync<OrderLineDetails>(
            new CommandDefinition(linesSql, new { Id = id }, cancellationToken: cancellationToken));
        return new OrderDetails(order.Id, order.CustomerEmail, order.Status, order.Total,
            order.CreatedAtUtc, lines.AsList());
    }

    public async Task<PagedResult<OrderSummary>> Search(OrderSearch query, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT o.Id, o.CustomerEmail, o.Status, o.CreatedAtUtc,
                   COALESCE((SELECT SUM(Quantity * UnitPrice) FROM OrderLines WHERE OrderId = o.Id), 0) AS Total
            FROM Orders o
            WHERE @Status IS NULL OR o.Status = @Status
            ORDER BY o.CreatedAtUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;

            SELECT COUNT(*) FROM Orders WHERE @Status IS NULL OR Status = @Status;
            """;
        var parameters = new
        {
            query.Status,
            Offset = (query.Page - 1) * query.PageSize,
            query.PageSize
        };

        using var connection = CreateConnection();
        using var results = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        var orders = (await results.ReadAsync<OrderSummary>()).AsList();
        var total = await results.ReadSingleAsync<int>();
        return new PagedResult<OrderSummary>(orders, query.Page, query.PageSize, total);
    }

    private sealed record OrderRow(
        Guid Id, string CustomerEmail, string Status, decimal Total, DateTimeOffset CreatedAtUtc);
}
