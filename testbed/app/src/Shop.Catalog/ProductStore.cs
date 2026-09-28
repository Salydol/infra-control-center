using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Prometheus;
using Shop.Common;
using StackExchange.Redis;

namespace Shop.Catalog;

public sealed record Product(long Id, string Name, string Category, decimal Price, int Stock);

public sealed record ReserveRow(decimal Price, bool Reserved);

public sealed class ProductStore(
    NpgsqlDataSource db,
    IConnectionMultiplexer redis,
    IOptionsMonitor<CacheOptions> cache,
    ILogger<ProductStore> logger)
{
    private static readonly Counter CacheRequests = Metrics.CreateCounter(
        "shop_cache_requests_total", "Обращения к кэшу товаров", "result");

    public async Task<Product?> GetAsync(long id)
    {
        var options = cache.CurrentValue;
        var key = $"product:{id}";

        if (options.Enabled)
        {
            try
            {
                var cached = await DependencyMetrics.TrackAsync("redis", "get",
                    async () => await redis.GetDatabase().StringGetAsync(key));
                if (cached.HasValue)
                {
                    CacheRequests.WithLabels("hit").Inc();
                    return JsonSerializer.Deserialize<Product>((string)cached!);
                }
                CacheRequests.WithLabels("miss").Inc();
            }
            catch (RedisException ex)
            {
                // Кэш недоступен — работаем напрямую с БД.
                CacheRequests.WithLabels("error").Inc();
                logger.LogWarning("Cache read failed for {Key}: {Error}", key, ex.Message);
            }
        }

        var product = await DependencyMetrics.TrackAsync("postgres", "product_by_id", async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<Product>(
                "select id, name, category, price, stock from products where id = @id", new { id });
        });

        if (product is not null && options.Enabled)
            await TryCacheAsync(key, product, options.TtlSeconds);

        return product;
    }

    public async Task<IReadOnlyList<Product>> ListAsync(string? search, int page, int size)
    {
        return (await DependencyMetrics.TrackAsync("postgres", "product_list", async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            return await conn.QueryAsync<Product>(
                """
                select id, name, category, price, stock from products
                where @search is null or name ilike '%' || @search || '%'
                order by id
                limit @size offset @offset
                """,
                new { search, size, offset = (page - 1) * size });
        })).AsList();
    }

    /// <summary>Списывает остаток; null — товара нет, false — не хватает.</summary>
    public async Task<(bool Found, bool Reserved, decimal Price)> ReserveAsync(long id, int quantity)
    {
        var row = await DependencyMetrics.TrackAsync("postgres", "product_reserve", async () =>
        {
            await using var conn = await db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<ReserveRow>(
                """
                with upd as (
                    update products set stock = stock - @quantity
                    where id = @id and stock >= @quantity
                    returning price)
                select coalesce((select price from upd), p.price) as price,
                       exists(select 1 from upd) as reserved
                from products p where p.id = @id
                """,
                new { id, quantity });
        });

        if (row is null)
            return (false, false, 0);

        if (row.Reserved)
            await TryInvalidateAsync($"product:{id}");

        return (true, row.Reserved, row.Price);
    }

    private async Task TryCacheAsync(string key, Product product, int ttlSeconds)
    {
        try
        {
            await DependencyMetrics.TrackAsync("redis", "set", () => redis.GetDatabase()
                .StringSetAsync(key, JsonSerializer.Serialize(product), TimeSpan.FromSeconds(Math.Max(ttlSeconds, 1))));
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Cache write failed for {Key}: {Error}", key, ex.Message);
        }
    }

    private async Task TryInvalidateAsync(string key)
    {
        try
        {
            await DependencyMetrics.TrackAsync("redis", "del", () => redis.GetDatabase().KeyDeleteAsync(key));
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Cache invalidation failed for {Key}: {Error}", key, ex.Message);
        }
    }
}
