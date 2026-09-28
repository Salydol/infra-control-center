using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Icc.ControlPlane.Agents;
using Icc.ControlPlane.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Icc.ControlPlane.Tests;

/// <summary>
/// Центр с настоящим PostgreSQL (Testcontainers) и временным каталогом PKI.
/// TestServer не поддерживает TLS, поэтому личность агента подменяется:
/// проверка самого сертификата покрыта отдельными тестами PKI.
/// </summary>
public sealed class ControlPlaneFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _pkiDir = Path.Combine(Path.GetTempPath(), $"icc-pki-{Guid.NewGuid():N}");

    public FakeAgentIdentity Identity { get; } = new();
    public CapturingHandler VictoriaMetrics { get; } = new();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Pki:Directory", _pkiDir);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAgentIdentity>(Identity);
            services.AddHttpClient<VictoriaMetricsWriter>().ConfigurePrimaryHttpMessageHandler(() => VictoriaMetrics);
        });
    }

    public GrpcChannel CreateChannel() =>
        GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_pkiDir))
            Directory.Delete(_pkiDir, recursive: true);
    }
}

public sealed class FakeAgentIdentity : IAgentIdentity
{
    public Guid? AgentId { get; set; }

    public Guid? Resolve(ServerCallContext context) => AgentId;
}

/// <summary>Подменяет VictoriaMetrics: запоминает тела запросов.</summary>
public sealed class CapturingHandler : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.RequestUri!.AbsolutePath + "\n" + await request.Content!.ReadAsStringAsync(ct);
        lock (Bodies)
            Bodies.Add(body);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}
