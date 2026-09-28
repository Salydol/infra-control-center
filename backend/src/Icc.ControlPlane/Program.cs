using Icc.ControlPlane.Agents;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGrpcService<AgentGatewayService>();
app.MapHealthChecks("/healthz");
app.MapGet("/api/version", () => new { name = "icc-control-plane", version = ThisAssembly.Version });

app.Run();

internal static class ThisAssembly
{
    public static readonly string Version =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

// Нужен WebApplicationFactory в интеграционных тестах.
public partial class Program;
