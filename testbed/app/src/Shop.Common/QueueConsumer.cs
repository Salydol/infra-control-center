using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Shop.Common;

/// <summary>
/// Фоновый потребитель очереди. Prefetch и параллелизм читаются при старте
/// (меняются только рестартом). Ошибка обработки: первая — сообщение
/// возвращается в очередь, повторная — уходит в dead-letter.
/// </summary>
public abstract class QueueConsumer<T>(RabbitBus bus, ILogger logger) : BackgroundService
{
    protected RabbitBus Bus { get; } = bus;
    protected ILogger Logger { get; } = logger;

    private static readonly Counter Processed = Metrics.CreateCounter(
        "shop_messages_processed_total", "Обработанные сообщения очереди", "queue", "result");

    private static readonly Histogram Duration = Metrics.CreateHistogram(
        "shop_message_processing_seconds", "Время обработки сообщения", new HistogramConfiguration
        {
            LabelNames = ["queue"],
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 12),
        });

    protected abstract string Queue { get; }
    protected abstract ushort Prefetch { get; }
    protected abstract ushort Concurrency { get; }

    protected abstract Task HandleAsync(T message, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await Bus.GetConnectionAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(false, false, consumerDispatchConcurrency: Concurrency),
            stoppingToken);
        await channel.BasicQosAsync(0, Prefetch, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) => await OnReceivedAsync(channel, ea, stoppingToken);
        await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);

        Logger.LogInformation("Consuming {Queue} with prefetch={Prefetch} concurrency={Concurrency}",
            Queue, Prefetch, Concurrency);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        await channel.CloseAsync();
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        using var timer = Duration.WithLabels(Queue).NewTimer();
        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(ea.Body.Span);
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Malformed message {MessageId} in {Queue}", ea.BasicProperties.MessageId, Queue);
            Processed.WithLabels(Queue, "malformed").Inc();
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
            return;
        }

        try
        {
            await HandleAsync(message!, ct);
            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
            Processed.WithLabels(Queue, "ok").Inc();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var requeue = !ea.Redelivered;
            Logger.LogError(ex, "Failed to process message {MessageId} from {Queue}, requeue={Requeue}",
                ea.BasicProperties.MessageId, Queue, requeue);
            Processed.WithLabels(Queue, requeue ? "retry" : "dead").Inc();
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue, ct);
        }
    }
}
