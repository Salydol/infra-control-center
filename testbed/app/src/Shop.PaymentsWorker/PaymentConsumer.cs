using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Shop.Common;

namespace Shop.PaymentsWorker;

public sealed class PaymentConsumer(
    RabbitBus bus,
    NpgsqlDataSource db,
    PaymentProvider provider,
    IOptions<PaymentOptions> options,
    ILogger<PaymentConsumer> logger) : QueueConsumer<OrderCreated>(bus, logger)
{
    protected override string Queue => Routing.PaymentsQueue;
    protected override ushort Prefetch => options.Value.Prefetch;
    protected override ushort Concurrency => options.Value.Concurrency;

    protected override async Task HandleAsync(OrderCreated message, CancellationToken ct)
    {
        string status;
        string? error = null;
        try
        {
            await provider.ChargeAsync(message.OrderId, message.Amount, ct);
            status = "paid";
        }
        catch (PaymentDeclinedException ex)
        {
            status = "payment_declined";
            error = ex.Message;
        }

        var paidAt = await DependencyMetrics.TrackAsync("postgres", "payment_save", async () =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(
                "insert into payments (order_id, amount, status, error) values (@OrderId, @Amount, @status, @error)",
                new { message.OrderId, message.Amount, status, error }, tx);
            var ts = await conn.ExecuteScalarAsync<DateTime?>(
                """
                update orders set status = @status, paid_at = case when @status = 'paid' then now() end
                where id = @OrderId returning paid_at
                """,
                new { message.OrderId, status }, tx);
            await tx.CommitAsync(ct);
            return ts;
        });

        if (status == "paid")
        {
            await DependencyMetrics.TrackAsync("rabbitmq", "publish", () => Bus.PublishAsync(Routing.OrderPaidKey,
                new OrderPaid(message.OrderId, message.CustomerId, message.Amount, paidAt ?? DateTime.UtcNow), ct));
            Logger.LogInformation("Order {OrderId} paid, amount {Amount}", message.OrderId, message.Amount);
        }
        else
        {
            Logger.LogWarning("Order {OrderId} payment declined: {Reason}", message.OrderId, error);
        }
    }
}
