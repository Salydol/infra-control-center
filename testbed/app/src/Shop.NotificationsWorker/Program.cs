using Shop.Common;
using Shop.NotificationsWorker;

var builder = WebApplication.CreateBuilder(args);
builder.AddShopDefaults("notifications-worker");

builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));
builder.Services.AddShopDatabase(builder.Configuration);
builder.Services.AddShopRedis(builder.Configuration);
builder.Services.AddShopRabbit(builder.Configuration);
builder.Services.AddHostedService<NotificationConsumer>();

var app = builder.Build();
app.UseShopDefaults();
app.Run();
