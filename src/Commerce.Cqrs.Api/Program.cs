using Commerce.Cqrs.Application;
using Commerce.Cqrs.Domain;
using Commerce.Cqrs.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new() { Title = "Commerce CQRS API", Version = "v1" }));
builder.Services.AddHealthChecks().AddDbContextCheck<OrdersDbContext>("sql");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("commerce-cqrs-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseSwagger();
app.UseSwaggerUI();
app.MapHealthChecks("/health");

var orders = app.MapGroup("/api/orders").WithTags("Orders");

orders.MapPost("/", async (
    CreateOrderRequest request,
    ICommandHandler<CreateOrder, Guid> handler,
    CancellationToken cancellationToken) =>
{
    var command = new CreateOrder(
        request.Lines.Select(line =>
            new CreateOrderLine(line.Sku, line.ProductName, line.Quantity, line.UnitPrice)).ToArray(),
        request.CustomerEmail);
    var id = await handler.Handle(command, cancellationToken);
    return Results.CreatedAtRoute("GetOrder", new { id }, new { id });
})
.WithName("CreateOrder")
.Produces(StatusCodes.Status201Created)
.ProducesProblem(StatusCodes.Status400BadRequest);

orders.MapGet("/{id:guid}", async (
    Guid id,
    IQueryHandler<GetOrder, OrderDetails?> handler,
    CancellationToken cancellationToken) =>
{
    var order = await handler.Handle(new GetOrder(id), cancellationToken);
    return order is null ? Results.NotFound() : Results.Ok(order);
})
.WithName("GetOrder")
.Produces<OrderDetails>()
.Produces(StatusCodes.Status404NotFound);

orders.MapGet("/", async (
    string? status,
    int page,
    int pageSize,
    IQueryHandler<SearchOrders, PagedResult<OrderSummary>> handler,
    CancellationToken cancellationToken) =>
    Results.Ok(await handler.Handle(new SearchOrders(status, page, pageSize), cancellationToken)))
.WithName("SearchOrders")
.Produces<PagedResult<OrderSummary>>();

orders.MapPost("/{id:guid}/confirm", async (
    Guid id,
    ICommandHandler<ConfirmOrder, bool> handler,
    CancellationToken cancellationToken) =>
{
    await handler.Handle(new ConfirmOrder(id), cancellationToken);
    return Results.NoContent();
})
.WithName("ConfirmOrder")
.Produces(StatusCodes.Status204NoContent);

orders.MapPost("/{id:guid}/cancel", async (
    Guid id,
    CancelOrderRequest request,
    ICommandHandler<CancelOrder, bool> handler,
    CancellationToken cancellationToken) =>
{
    await handler.Handle(new CancelOrder(id, request.Reason), cancellationToken);
    return Results.NoContent();
})
.WithName("CancelOrder")
.Produces(StatusCodes.Status204NoContent);

if (builder.Configuration.GetValue("Database:ApplyMigrations", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
}

app.Run();

public sealed record CreateOrderRequest(string CustomerEmail, IReadOnlyCollection<CreateOrderLineRequest> Lines);
public sealed record CreateOrderLineRequest(string Sku, string ProductName, int Quantity, decimal UnitPrice);
public sealed record CancelOrderRequest(string Reason);

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            DomainException or ArgumentException => (StatusCodes.Status400BadRequest, "Business rule rejected"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status < 500 || environment.IsDevelopment() ? exception.Message : null
            },
            Exception = exception
        });
    }
}

public partial class Program;
