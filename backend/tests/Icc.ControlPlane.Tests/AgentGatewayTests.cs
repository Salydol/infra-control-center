using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Icc.Contracts.Agent.V1;
using Icc.ControlPlane.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Icc.ControlPlane.Tests;

public class AgentGatewayTests(ControlPlaneFactory factory) : IClassFixture<ControlPlaneFactory>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_WithValidToken_IssuesCertificateSignedByCa()
    {
        var token = await CreateTokenAsync();
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());

        var response = await client.RegisterAsync(NewRequest(token), cancellationToken: Ct);

        var certificate = X509Certificate2.CreateFromPem(response.ClientCertPem);
        var pki = factory.Services.GetRequiredService<AgentPki>();
        Assert.True(pki.ValidateAgentCertificate(certificate));
        Assert.Equal(response.AgentId, AgentPki.AgentIdOf(certificate)?.ToString());
        Assert.Equal(pki.CaCertificatePem, response.CaCertPem);
    }

    [Fact]
    public async Task Register_TokenIsSingleUse()
    {
        var token = await CreateTokenAsync();
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());
        await client.RegisterAsync(NewRequest(token), cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RegisterAsync(NewRequest(token), cancellationToken: Ct));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("icc1.00000000.00000000000000000000000000000000.0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Register_WithInvalidToken_IsDenied(string token)
    {
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RegisterAsync(NewRequest(token), cancellationToken: Ct));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task Register_WithForeignCaHash_IsDenied()
    {
        var parsed = ParsedJoinToken.Parse(await CreateTokenAsync())!;
        var foreign = parsed with { CaHash = new string('a', 64) };
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RegisterAsync(NewRequest(foreign.ToString()), cancellationToken: Ct));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutCertificate_IsUnauthenticated()
    {
        factory.Identity.AgentId = null;
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());

        using var call = client.Connect(cancellationToken: Ct);

        // Центр отказывает до чтения сообщений: ошибка может прийти уже на записи.
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(new ConnectRequest { Seq = 1, Hello = new Hello { AgentId = "x" } }, Ct);
            await call.ResponseStream.MoveNext(Ct);
        });
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_HelloWithForeignAgentId_IsDenied()
    {
        var agentId = await RegisterAsync();
        factory.Identity.AgentId = agentId;
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());

        using var call = client.Connect(cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new ConnectRequest
        {
            Seq = 1,
            Hello = new Hello { AgentId = Guid.NewGuid().ToString() },
        }, Ct);

        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(Ct));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_StoresInventory_WritesMetrics_AndAgentIsOnline()
    {
        var agentId = await RegisterAsync();
        factory.Identity.AgentId = agentId;
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());

        using var call = client.Connect(cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new ConnectRequest
        {
            Seq = 1,
            Hello = new Hello { AgentId = agentId.ToString(), AgentVersion = "test", Host = new HostInfo { Hostname = "node9" } },
        }, Ct);
        await call.RequestStream.WriteAsync(new ConnectRequest
        {
            Seq = 2,
            Inventory = new InventorySnapshot
            {
                CollectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                Containers = { new Container { Id = "abc", Name = "shop-api-1", ComposeProject = "shop", ComposeService = "api" } },
            },
        }, Ct);
        await call.RequestStream.WriteAsync(new ConnectRequest
        {
            Seq = 3,
            Metrics = new MetricBatch
            {
                Samples =
                {
                    new MetricSample
                    {
                        Name = "host_cpu_usage_ratio", Value = 0.25,
                        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow), Labels = { ["host"] = "node9" },
                    },
                },
            },
        }, Ct);
        await call.RequestStream.CompleteAsync();

        var responses = await call.ResponseStream.ReadAllAsync(Ct).ToListAsync(Ct);
        Assert.Equal(ConnectResponse.PayloadOneofCase.HelloAck, responses[0].PayloadCase);
        Assert.Equal([1UL, 2UL, 3UL], responses.Skip(1).Select(r => r.Ack.Seq));

        using var http = factory.CreateClient();
        var agents = await http.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        var agent = agents.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == agentId);
        Assert.Equal("online", agent.GetProperty("status").GetString());
        Assert.Equal("node9", agent.GetProperty("hostname").GetString());

        var inventory = await http.GetFromJsonAsync<JsonElement>($"/api/agents/{agentId}/inventory", Ct);
        Assert.Equal("shop-api-1", inventory.GetProperty("containers")[0].GetProperty("name").GetString());

        var written = factory.VictoriaMetrics.Bodies.Single(b => b.Contains(agentId.ToString()));
        Assert.StartsWith("/api/v1/import\n", written);
        var line = JsonDocument.Parse(written.Split('\n')[1]).RootElement;
        Assert.Equal("host_cpu_usage_ratio", line.GetProperty("metric").GetProperty("__name__").GetString());
        Assert.Equal("node9", line.GetProperty("metric").GetProperty("host").GetString());
        Assert.Equal(0.25, line.GetProperty("values")[0].GetDouble());
    }

    private async Task<string> CreateTokenAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JoinTokenService>()
            .CreateAsync(TimeSpan.FromHours(1), "test", Ct);
    }

    private async Task<Guid> RegisterAsync()
    {
        var client = new AgentService.AgentServiceClient(factory.CreateChannel());
        var response = await client.RegisterAsync(NewRequest(await CreateTokenAsync()), cancellationToken: Ct);
        return Guid.Parse(response.AgentId);
    }

    private static RegisterRequest NewRequest(string token)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=ignored-by-center", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        return new RegisterRequest
        {
            Token = token,
            CsrPem = csr,
            AgentVersion = "test",
            Host = new HostInfo { Hostname = "test-host" },
        };
    }
}
