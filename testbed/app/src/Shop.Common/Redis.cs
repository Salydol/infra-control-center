using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Shop.Common;

public sealed class RedisOptions
{
    public string Endpoint { get; set; } = "redis:6379";
    public int ConnectTimeoutMs { get; set; } = 2000;
    public int SyncTimeoutMs { get; set; } = 1000;
}

public static class RedisExtensions
{
    public static IServiceCollection AddShopRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Redis").Get<RedisOptions>() ?? new RedisOptions();
        var config = ConfigurationOptions.Parse(options.Endpoint);
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = options.ConnectTimeoutMs;
        config.SyncTimeout = options.SyncTimeoutMs;
        config.AsyncTimeout = options.SyncTimeoutMs;

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config));
        return services;
    }
}
