using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Shop.Common;

namespace Shop.Catalog;

/// <summary>Периодически пополняет остатки, чтобы нагрузка не исчерпала склад.</summary>
public sealed class RestockService(
    NpgsqlDataSource db,
    IOptionsMonitor<CatalogOptions> options,
    ILogger<RestockService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            try
            {
                var restocked = await DependencyMetrics.TrackAsync("postgres", "restock", async () =>
                {
                    await using var conn = await db.OpenConnectionAsync(stoppingToken);
                    return await conn.ExecuteAsync(
                        "update products set stock = @to where stock < @threshold",
                        new { to = o.RestockTo, threshold = o.RestockThreshold });
                });
                if (restocked > 0)
                    logger.LogInformation("Restocked {Count} products", restocked);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Restock failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(o.RestockIntervalSeconds, 1)), stoppingToken);
        }
    }
}
