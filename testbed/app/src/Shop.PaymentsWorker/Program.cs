using Shop.Common;
using Shop.PaymentsWorker;

var builder = WebApplication.CreateBuilder(args);
builder.AddShopDefaults("payments-worker");

builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
builder.Services.AddShopDatabase(builder.Configuration);
builder.Services.AddShopRabbit(builder.Configuration);
builder.Services.AddSingleton<PaymentProvider>();
builder.Services.AddHostedService<PaymentConsumer>();

var app = builder.Build();
app.UseShopDefaults();
app.Run();
