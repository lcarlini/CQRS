using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Commerce.Cqrs.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Commerce.Cqrs.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Orders")
            ?? throw new InvalidOperationException("ConnectionStrings:Orders is required.");

        services.AddDbContext<OrdersDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<IOrderWriteRepository, OrderWriteRepository>();
        services.AddScoped<IOrderReadStore>(_ => new SqlOrderReadStore(connectionString));

        services.Configure<ServiceBusOptions>(configuration.GetSection(ServiceBusOptions.SectionName));
        services.AddSingleton<IEventPublisher, AzureServiceBusPublisher>();
        services.AddHostedService<OutboxPublisher>();
        return services;
    }
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";
    public string? ConnectionString { get; init; }
    public string? FullyQualifiedNamespace { get; init; }
    public string TopicName { get; init; } = "order-events";
}

internal interface IEventPublisher
{
    Task Publish(OutboxMessage message, CancellationToken cancellationToken);
}

internal sealed class AzureServiceBusPublisher(
    IOptions<ServiceBusOptions> options,
    ILogger<AzureServiceBusPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusOptions _options = options.Value;
    private ServiceBusClient? _client;
    private ServiceBusSender? _sender;

    public async Task Publish(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString)
            && string.IsNullOrWhiteSpace(_options.FullyQualifiedNamespace))
        {
            logger.LogInformation(
                "Service Bus is not configured; event {EventType} ({EventId}) remains local",
                message.Type, message.Id);
            return;
        }

        _client ??= !string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? new ServiceBusClient(_options.ConnectionString)
            : new ServiceBusClient(_options.FullyQualifiedNamespace, new DefaultAzureCredential());
        _sender ??= _client.CreateSender(_options.TopicName);
        var busMessage = new ServiceBusMessage(message.Payload)
        {
            MessageId = message.Id.ToString(),
            Subject = message.Type,
            CorrelationId = message.AggregateId.ToString(),
            ContentType = "application/json"
        };
        busMessage.ApplicationProperties["occurredAtUtc"] = message.OccurredAtUtc.ToString("O");
        await _sender.SendMessageAsync(busMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}

internal sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            await PublishBatch(stoppingToken);
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task PublishBatch(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            var messages = await dbContext.OutboxMessages
                .Where(message => message.ProcessedAtUtc == null)
                .OrderBy(message => message.OccurredAtUtc)
                .Take(20)
                .ToListAsync(cancellationToken);

            foreach (var message in messages)
            {
                try
                {
                    await publisher.Publish(message, cancellationToken);
                    message.ProcessedAtUtc = DateTimeOffset.UtcNow;
                    message.Error = null;
                }
                catch (Exception exception)
                {
                    message.Error = exception.Message[..Math.Min(exception.Message.Length, 1000)];
                    logger.LogError(exception, "Failed to publish outbox message {MessageId}", message.Id);
                }
            }

            if (messages.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Outbox polling failed; retrying on the next interval");
        }
    }
}
