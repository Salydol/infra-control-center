using Icc.ControlPlane;
using Icc.ControlPlane.Agents;
using Icc.ControlPlane.Data;
using Icc.ControlPlane.Telemetry;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PkiOptions>(builder.Configuration.GetSection("Pki"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton(sp => AgentPki.LoadOrCreate(sp.GetRequiredService<IOptions<PkiOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<IccDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddScoped<JoinTokenService>();
builder.Services.AddSingleton<IAgentIdentity, CertificateAgentIdentity>();
builder.Services.AddHttpClient<VictoriaMetricsWriter>((sp, http) =>
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<StorageOptions>>().Value.VictoriaMetricsUrl));

builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = 16 * 1024 * 1024);
builder.Services.AddHealthChecks();

// Порт агентов: только HTTPS/HTTP2 с сертификатом нашего CA; клиентский
// сертификат необязателен на уровне TLS (его нет у ещё не зарегистрированного
// агента), Connect сам требует его и проверяет.
var agentsPort = builder.Configuration.GetValue("Agents:Port", 9090);
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.ListenAnyIP(agentsPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
        var pki = kestrel.ApplicationServices.GetRequiredService<AgentPki>();
        listen.UseHttps(https =>
        {
            https.ServerCertificate = pki.ServerCertificate;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => pki.ValidateAgentCertificate(certificate);
        });
    });
});

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IccDbContext>().Database.MigrateAsync();
}

if (Cli.IsCommand(args))
    return await Cli.RunAsync(app, args);

app.MapGrpcService<AgentGatewayService>();
app.MapHealthChecks("/healthz");
app.MapGet("/api/version", () => new { name = "icc-control-plane", version = ThisAssembly.Version });
app.MapAgentEndpoints();

await app.RunAsync();
return 0;

internal static class ThisAssembly
{
    public static readonly string Version =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

// Нужен WebApplicationFactory в интеграционных тестах.
public partial class Program;
