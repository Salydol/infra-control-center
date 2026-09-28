using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Shop.Common;

public sealed class RabbitOptions
{
    public string Host { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "shop";
    public string Password { get; set; } = "";
}

/// <summary>
/// Одно соединение с RabbitMQ на процесс, с автоматическим восстановлением.
/// Первое подключение повторяется, пока брокер не станет доступен.
/// </summary>
public sealed class RabbitBus(IOptions<RabbitOptions> options, ServiceInfo service, ILogger<RabbitBus> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;

    public async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.User,
                Password = o.Password,
                ClientProvidedName = service.Name,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
            };

            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    _connection = await factory.CreateConnectionAsync(ct);
                    logger.LogInformation("Connected to RabbitMQ {Host}:{Port}", o.Host, o.Port);
                    await DeclareTopologyAsync(_connection, ct);
                    return _connection;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("RabbitMQ {Host}:{Port} unavailable: {Error}; retry in {Delay}s",
                        o.Host, o.Port, ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishAsync<T>(string routingKey, T message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString("N"),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        await _publishLock.WaitAsync(ct);
        try
        {
            if (_publishChannel is not { IsOpen: true })
            {
                var connection = await GetConnectionAsync(ct);
                _publishChannel = await connection.CreateChannelAsync(cancellationToken: ct);
            }

            await _publishChannel.BasicPublishAsync(Routing.Exchange, routingKey, mandatory: false, props, body, ct);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private static async Task DeclareTopologyAsync(IConnection connection, CancellationToken ct)
    {
        await using var ch = await connection.CreateChannelAsync(cancellationToken: ct);
        await ch.ExchangeDeclareAsync(Routing.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await ch.ExchangeDeclareAsync(Routing.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await ch.QueueDeclareAsync(Routing.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await ch.QueueBindAsync(Routing.DeadLetterQueue, Routing.DeadLetterExchange, "", cancellationToken: ct);

        var args = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = Routing.DeadLetterExchange };
        await ch.QueueDeclareAsync(Routing.PaymentsQueue, durable: true, exclusive: false, autoDelete: false, args, cancellationToken: ct);
        await ch.QueueBindAsync(Routing.PaymentsQueue, Routing.Exchange, Routing.OrderCreatedKey, cancellationToken: ct);
        await ch.QueueDeclareAsync(Routing.NotificationsQueue, durable: true, exclusive: false, autoDelete: false, args, cancellationToken: ct);
        await ch.QueueBindAsync(Routing.NotificationsQueue, Routing.Exchange, Routing.OrderPaidKey, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_publishChannel is not null)
            await _publishChannel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

public static class RabbitExtensions
{
    public static IServiceCollection AddShopRabbit(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitOptions>(configuration.GetSection("RabbitMq"));
        services.AddSingleton<RabbitBus>();
        return services;
    }
}
