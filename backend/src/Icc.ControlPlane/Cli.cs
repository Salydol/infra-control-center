using Icc.ControlPlane.Agents;

namespace Icc.ControlPlane;

/// <summary>
/// Служебные команды администратора, выполняются тем же образом:
/// <c>docker compose exec backend dotnet Icc.ControlPlane.dll agents create-token --ttl-hours 24</c>
/// </summary>
public static class Cli
{
    public static bool IsCommand(string[] args) => args is ["agents", ..];

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        switch (args)
        {
            case ["agents", "create-token", .. var options]:
            {
                var ttlHours = 24;
                string? description = null;
                for (var i = 0; i < options.Length - 1; i++)
                {
                    if (options[i] == "--ttl-hours")
                        ttlHours = int.Parse(options[i + 1]);
                    else if (options[i] == "--description")
                        description = options[i + 1];
                }

                await using var scope = app.Services.CreateAsyncScope();
                var tokens = scope.ServiceProvider.GetRequiredService<JoinTokenService>();
                var token = await tokens.CreateAsync(TimeSpan.FromHours(ttlHours), description, CancellationToken.None);
                Console.WriteLine(token);
                return 0;
            }
            default:
                Console.Error.WriteLine("usage: agents create-token [--ttl-hours N] [--description TEXT]");
                return 2;
        }
    }
}
