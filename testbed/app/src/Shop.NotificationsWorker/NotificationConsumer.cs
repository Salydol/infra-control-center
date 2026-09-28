using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Shop.Common;
using StackExchange.Redis;

namespace Shop.NotificationsWorker;

public sealed class NotificationOptions
{
    // Меняются только рестартом.
    public ushort Prefetch { get; set; } = 20;
    public ushort Concurrency { get; set; } = 2;

    // Применяются на лету.
    public string Channel { get; set; } = "email";
    public int SendDelayMs { get; set; } = 30;
    public int DedupTtlSeconds { get; set; } = 3600;
}

/// <summary>
/// Отправляет уведомление об оплате. Дубликаты (повторная доставка сообщения)
/// отсекаются ключом в Redis; без Redis обработка падает и сообщение
/// повторяется — уведомления важнее не задвоить, чем отправить быстро.
/// </summary>
public sealed class NotificationConsumer(
    RabbitBus bus,
    NpgsqlDataSource db,
    IConnectionMultiplexer redis,
    IOptions<NotificationOptions> startupOptions,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<NotificationConsumer> logger) : QueueConsumer<OrderPaid>(bus, logger)
{
    protected override string Queue => Routing.NotificationsQueue;
    protected override ushort Prefetch => startupOptions.Value.Prefetch;
    protected override ushort Concurrency => startupOptions.Value.Concurrency;

    // Используется только дефектной сборкой memory-leak: «кэш шаблонов», который никогда не чистится.
    private static readonly List<byte[]> RenderedTemplates = [];

    protected override async Task HandleAsync(OrderPaid message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (BuildFault.Is("memory-leak"))
        {
            var rendered = new byte[1024 * 1024];
            Random.Shared.NextBytes(rendered);
            lock (RenderedTemplates)
                RenderedTemplates.Add(rendered);
        }

        var first = await DependencyMetrics.TrackAsync("redis", "dedup", () => redis.GetDatabase().StringSetAsync(
            $"notified:{message.OrderId}", 1, TimeSpan.FromSeconds(o.DedupTtlSeconds), When.NotExists));
        if (!first)
        {
            Logger.LogInformation("Duplicate notification for order {OrderId} skipped", message.OrderId);
            return;
        }

        // Имитация отправки во внешний канал (почта, SMS).
        await DependencyMetrics.TrackAsync("notification_channel", o.Channel, () => Task.Delay(o.SendDelayMs, ct));

        await DependencyMetrics.TrackAsync("postgres", "notification_insert", async () =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(
                "insert into notifications (order_id, customer_id, channel) values (@OrderId, @CustomerId, @channel)",
                new { message.OrderId, message.CustomerId, channel = o.Channel });
        });

        Logger.LogInformation("Notification sent for order {OrderId} via {Channel}", message.OrderId, o.Channel);
    }
}
