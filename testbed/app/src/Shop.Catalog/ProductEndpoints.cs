using Microsoft.Extensions.Options;
using Shop.Common;

namespace Shop.Catalog;

public sealed record ReserveRequest(int Quantity);

public sealed record ReserveResponse(long ProductId, int Quantity, decimal UnitPrice);

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this WebApplication app)
    {
        app.MapGet("/products/{id:long}", async (long id, ProductStore store) =>
        {
            var product = await store.GetAsync(id);
            if (product is not null && BuildFault.Is("unindexed-query"))
                await store.CountOrdersAsync(id);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });

        app.MapGet("/products", async (string? search, int? page, int? size,
            ProductStore store, IOptionsMonitor<CatalogOptions> options) =>
        {
            var pageSize = Math.Clamp(size ?? 20, 1, options.CurrentValue.MaxPageSize);
            return Results.Ok(await store.ListAsync(search, Math.Max(page ?? 1, 1), pageSize));
        });

        app.MapPost("/products/{id:long}/reserve", async (long id, ReserveRequest request,
            ProductStore store, ILogger<ProductStore> logger) =>
        {
            if (request.Quantity <= 0)
                return Results.BadRequest("quantity must be positive");

            var (found, reserved, price) = await store.ReserveAsync(id, request.Quantity);
            if (!found)
                return Results.NotFound();
            if (!reserved)
            {
                logger.LogInformation("Insufficient stock for product {ProductId}, requested {Quantity}", id, request.Quantity);
                return Results.Conflict("insufficient stock");
            }

            return Results.Ok(new ReserveResponse(id, request.Quantity, price));
        });
    }
}
