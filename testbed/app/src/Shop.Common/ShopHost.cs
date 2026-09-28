using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Prometheus;

namespace Shop.Common;

public static class ShopHost
{
    /// <summary>
    /// Общая настройка сервиса магазина.
    ///
    /// Порядок источников настроек: appsettings.json из образа → config/appsettings.json,
    /// смонтированный с хоста (правится инжектором сбоев и «вручную по SSH») →
    /// переменные окружения (из compose и env-файлов). Файл с хоста перечитывается
    /// на лету; настройки, читаемые через IOptionsMonitor, применяются без рестарта.
    /// </summary>
    public static WebApplicationBuilder AddShopDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Configuration
            .AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            o.ColorBehavior = LoggerColorBehavior.Disabled;
        });

        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(new ServiceInfo(serviceName));

        Metrics.DefaultRegistry.SetStaticLabels(new Dictionary<string, string> { ["service"] = serviceName });

        return builder;
    }

    public static WebApplication UseShopDefaults(this WebApplication app)
    {
        app.UseHttpMetrics();
        app.MapHealthChecks("/healthz");
        app.MapMetrics("/metrics");
        return app;
    }
}

public sealed record ServiceInfo(string Name);
