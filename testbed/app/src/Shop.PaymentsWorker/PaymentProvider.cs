using Microsoft.Extensions.Options;
using Shop.Common;

namespace Shop.PaymentsWorker;

public sealed class PaymentOptions
{
    // Меняются только рестартом (читаются при старте потребителя).
    public ushort Prefetch { get; set; } = 20;
    public ushort Concurrency { get; set; } = 4;

    // Применяются на лету.
    public int ProviderLatencyMs { get; set; } = 80;
    public int ProviderTimeoutMs { get; set; } = 1000;
    public double ProviderDeclineRate { get; set; } = 0.03;
}

public sealed class PaymentDeclinedException(string reason) : Exception(reason);

/// <summary>
/// Имитация внешнего платёжного провайдера: задержка с разбросом и доля
/// отказов. Превышение ProviderTimeoutMs — техническая ошибка (повтор),
/// отказ провайдера — бизнес-результат (заказ не оплачен).
/// </summary>
public sealed class PaymentProvider(IOptionsMonitor<PaymentOptions> options)
{
    public async Task ChargeAsync(long orderId, decimal amount, CancellationToken ct)
    {
        var o = options.CurrentValue;
        // Логнормальный разброс: большинство ответов около медианы, редкие — в разы дольше.
        var jitter = Math.Exp(Random.Shared.NextDouble() * 1.2 - 0.6);
        var latency = TimeSpan.FromMilliseconds(o.ProviderLatencyMs * jitter);

        await DependencyMetrics.TrackAsync("payment_provider", "charge", async () =>
        {
            if (latency.TotalMilliseconds > o.ProviderTimeoutMs)
            {
                await Task.Delay(o.ProviderTimeoutMs, ct);
                throw new TimeoutException($"payment provider timeout after {o.ProviderTimeoutMs}ms");
            }

            await Task.Delay(latency, ct);
        });

        if (Random.Shared.NextDouble() < o.ProviderDeclineRate)
            throw new PaymentDeclinedException("card declined");
    }
}
