using Shop.Api;
using Shop.Common;

var builder = WebApplication.CreateBuilder(args);
builder.AddShopDefaults("api");

builder.Services.Configure<CatalogClientOptions>(builder.Configuration.GetSection("Catalog"));
builder.Services.Configure<OrderOptions>(builder.Configuration.GetSection("Orders"));
builder.Services.AddShopDatabase(builder.Configuration);
builder.Services.AddShopRabbit(builder.Configuration);
builder.Services.AddHttpClient<CatalogClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);

var app = builder.Build();
app.UseShopDefaults();
app.MapOrderEndpoints();
app.Run();
