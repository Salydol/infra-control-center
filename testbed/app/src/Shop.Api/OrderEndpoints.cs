using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Prometheus;
using Shop.Common;

namespace Shop.Api;

public sealed class OrderOptions
{
    public int MaxQuantity { get; set; } = 10;
}

public sealed record CreateOrderRequest(long CustomerId, long ProductId, int Quantity);

public sealed record Order(long Id, long CustomerId, long ProductId, int Quantity, decimal Amount, string Status,
    DateTime CreatedAt, DateTime? PaidAt);

public static class OrderEndpoints
{
    private static readonly Counter OrdersCreated = Metrics.CreateCounter(
        "shop_orders_total", "Попытки создать заказ", "result");

    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapPost("/orders", CreateOrderAsync);

        app.MapGet("/orders/{id:long}", async (long id, NpgsqlDataSource db) =>
        {
            var order = await DependencyMetrics.TrackAsync("postgres", "order_by_id", async () =>
            {
                await using var conn = await db.OpenConnectionAsync();
                return await conn.QuerySingleOrDefaultAsync<Order>(
                    """
                    select id, customer_id as CustomerId, product_id as ProductId, quantity, amount, status,
                           created_at as CreatedAt, paid_at as PaidAt
                    from orders where id = @id
                    """, new { id });
            });
            return order is null ? Results.NotFound() : Results.Ok(order);
        });

        app.MapGet("/products/{id:long}", (long id, CatalogClient catalog, CancellationToken ct) =>
            ForwardAsync(catalog, $"/products/{id}", ct));

        app.MapGet("/products", (HttpRequest request, CatalogClient catalog, CancellationToken ct) =>
            ForwardAsync(catalog, "/products" + request.QueryString, ct));
    }

    private static async Task<IResult> CreateOrderAsync(CreateOrderRequest request, CatalogClient catalog,
        NpgsqlDataSource db, RabbitBus bus, IOptionsMonitor<OrderOptions> options, ILogger<CatalogClient> logger,
        CancellationToken ct)
    {
        if (request.Quantity <= 0 || request.Quantity > options.CurrentValue.MaxQuantity)
        {
            OrdersCreated.WithLabels("invalid").Inc();
            return Results.BadRequest($"quantity must be between 1 and {options.CurrentValue.MaxQuantity}");
        }

        ReserveOutcome outcome;
        Reservation? reservation;
        try
        {
            (outcome, reservation) = await catalog.ReserveAsync(request.ProductId, request.Quantity, ct);
        }
        catch (CatalogUnavailableException)
        {
            OrdersCreated.WithLabels("catalog_unavailable").Inc();
            return Results.Problem("catalog unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (outcome == ReserveOutcome.NotFound)
        {
            OrdersCreated.WithLabels("not_found").Inc();
            return Results.NotFound();
        }
        if (outcome == ReserveOutcome.InsufficientStock)
        {
            OrdersCreated.WithLabels("out_of_stock").Inc();
            return Results.Conflict("insufficient stock");
        }

        var amount = reservation!.UnitPrice * request.Quantity;
        var (orderId, createdAt) = await DependencyMetrics.TrackAsync("postgres", "order_insert", async () =>
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            return await conn.QuerySingleAsync<(long, DateTime)>(
                """
                insert into orders (customer_id, product_id, quantity, amount, status)
                values (@CustomerId, @ProductId, @Quantity, @amount, 'created')
                returning id, created_at
                """, new { request.CustomerId, request.ProductId, request.Quantity, amount });
        });

        try
        {
            await DependencyMetrics.TrackAsync("rabbitmq", "publish", () => bus.PublishAsync(Routing.OrderCreatedKey,
                new OrderCreated(orderId, request.CustomerId, request.ProductId, request.Quantity, amount, createdAt), ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to publish OrderCreated for order {OrderId}", orderId);
            OrdersCreated.WithLabels("publish_failed").Inc();
            return Results.Problem("order queued for retry", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        OrdersCreated.WithLabels("created").Inc();
        logger.LogInformation("Order {OrderId} created: customer {CustomerId}, product {ProductId} x{Quantity}, amount {Amount}",
            orderId, request.CustomerId, request.ProductId, request.Quantity, amount);
        return Results.Created($"/orders/{orderId}", new { id = orderId, amount });
    }

    private static async Task<IResult> ForwardAsync(CatalogClient catalog, string path, CancellationToken ct)
    {
        try
        {
            var (status, body) = await catalog.ForwardGetAsync(path, ct);
            return Results.Content(body, "application/json", statusCode: (int)status);
        }
        catch (CatalogUnavailableException)
        {
            return Results.Problem("catalog unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
