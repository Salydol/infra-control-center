using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Shop.Common;

public sealed class DatabaseOptions
{
    public string Host { get; set; } = "postgres";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "shop";
    public string User { get; set; } = "shop";
    public string Password { get; set; } = "";
    public int MaxPoolSize { get; set; } = 20;
    // Сколько ждать свободного соединения из пула, прежде чем упасть.
    public int ConnectionTimeoutSeconds { get; set; } = 5;
    public int CommandTimeoutSeconds { get; set; } = 10;
}

public static class DatabaseExtensions
{
    /// <summary>
    /// Пул соединений создаётся один раз при старте: изменение MaxPoolSize
    /// и адреса БД требует рестарта — как в большинстве реальных сервисов.
    /// </summary>
    public static IServiceCollection AddShopDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Name,
            Username = options.User,
            Password = options.Password,
            MaxPoolSize = options.MaxPoolSize,
            Timeout = options.ConnectionTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
        }.ConnectionString;

        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        return services;
    }
}
