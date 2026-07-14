using Commerce.Cqrs.Domain;

namespace Commerce.Cqrs.Tests;

public sealed class OrderTests
{
    [Fact]
    public void Create_calculates_total_and_records_event()
    {
        var order = Order.Create("CUSTOMER@example.com", [
            new OrderLine("book-1", "Architecture Patterns", 2, 49.90m),
            new OrderLine("mug-1", "Engineering Mug", 1, 18.50m)
        ]);

        Assert.Equal(118.30m, order.Total);
        Assert.Equal("customer@example.com", order.CustomerEmail);
        Assert.Single(order.DomainEvents);
        Assert.IsType<OrderCreated>(order.DomainEvents.Single());
    }

    [Fact]
    public void Confirm_transitions_draft_order_and_records_event()
    {
        var order = CreateOrder();

        order.Confirm();

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.IsType<OrderConfirmed>(order.DomainEvents.Last());
    }

    [Fact]
    public void Confirm_rejects_non_draft_order()
    {
        var order = CreateOrder();
        order.Confirm();

        var exception = Assert.Throws<DomainException>(order.Confirm);

        Assert.Contains("draft", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Line_rejects_non_positive_quantity(int quantity)
    {
        Assert.Throws<DomainException>(() => new OrderLine("sku", "Product", quantity, 10));
    }

    private static Order CreateOrder() =>
        Order.Create("customer@example.com", [new OrderLine("sku", "Product", 1, 10)]);
}
