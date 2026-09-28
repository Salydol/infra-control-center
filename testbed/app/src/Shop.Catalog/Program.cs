using Shop.Catalog;
using Shop.Common;

var builder = WebApplication.CreateBuilder(args);
builder.AddShopDefaults("catalog");

builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection("Catalog"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.AddShopDatabase(builder.Configuration);
builder.Services.AddShopRedis(builder.Configuration);
builder.Services.AddSingleton<ProductStore>();
builder.Services.AddHostedService<RestockService>();

var app = builder.Build();
app.UseShopDefaults();
app.MapProductEndpoints();
app.Run();
