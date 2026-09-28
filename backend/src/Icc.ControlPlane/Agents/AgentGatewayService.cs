using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Icc.Contracts.Agent.V1;
using Icc.ControlPlane.Data;
using Icc.ControlPlane.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Icc.ControlPlane.Agents;

/// <summary>
/// Точка входа агентов: регистрация по одноразовому токену и основной поток.
/// Поток принимается только от агента с действующим сертификатом нашего CA.
/// </summary>
public sealed class AgentGatewayService(
    IServiceScopeFactory scopes,
    AgentPki pki,
    IAgentIdentity identity,
    VictoriaMetricsWriter metrics,
    TimeProvider time,
    ILogger<AgentGatewayService> logger) : AgentService.AgentServiceBase
{
    private static readonly JsonFormatter InventoryJson = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    public override Task<GetCenterInfoResponse> GetCenterInfo(GetCenterInfoRequest request, ServerCallContext context) =>
        Task.FromResult(new GetCenterInfoResponse { CaCertPem = pki.CaCertificatePem });

    public override async Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IccDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<JoinTokenService>();

        var agentId = Guid.CreateVersion7();
        if (await tokens.RedeemAsync(request.Token, agentId, ct) is null)
        {
            logger.LogWarning("Rejected agent registration from {Host}: invalid, used or expired token",
                request.Host?.Hostname);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "invalid, used or expired join token"));
        }

        X509CertificateInfo issued;
        try
        {
            using var certificate = pki.SignAgentCsr(request.CsrPem, agentId);
            issued = new X509CertificateInfo(certificate.ExportCertificatePem(), certificate.SerialNumber,
                certificate.NotAfter.ToUniversalTime());
        }
        catch (CryptographicException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid CSR: {e.Message}"));
        }

        db.Agents.Add(new Agent
        {
            Id = agentId,
            Hostname = request.Host?.Hostname ?? "unknown",
            AgentVersion = request.AgentVersion,
            RegisteredAt = time.GetUtcNow(),
            CertificateSerial = issued.Serial,
            CertificateNotAfter = issued.NotAfter,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Agent {AgentId} registered for host {Host}", agentId, request.Host?.Hostname);
        return new RegisterResponse
        {
            AgentId = agentId.ToString(),
            ClientCertPem = issued.Pem,
            CaCertPem = pki.CaCertificatePem,
            CertExpiresAt = Timestamp.FromDateTimeOffset(issued.NotAfter),
        };
    }

    public override async Task Connect(
        IAsyncStreamReader<ConnectRequest> requestStream,
        IServerStreamWriter<ConnectResponse> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var agentId = identity.Resolve(context)
                      ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "agent certificate required"));

        if (!await requestStream.MoveNext(ct))
            return;
        var first = requestStream.Current;
        if (first.PayloadCase != ConnectRequest.PayloadOneofCase.Hello)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "first message must be Hello"));
        if (first.Hello.AgentId != agentId.ToString())
            throw new RpcException(new Status(StatusCode.PermissionDenied, "agent id does not match certificate"));

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IccDbContext>();
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, ct);
            if (agent is null || agent.Revoked)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "agent is unknown or revoked"));
            agent.AgentVersion = first.Hello.AgentVersion;
            if (first.Hello.Host?.Hostname is { Length: > 0 } hostname)
                agent.Hostname = hostname;
            agent.LastSeenAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Agent {AgentId} ({Host}) connected", agentId, first.Hello.Host?.Hostname);
        await responseStream.WriteAsync(new ConnectResponse { HelloAck = new HelloAck { Config = DefaultConfig() } }, ct);
        await responseStream.WriteAsync(new ConnectResponse { Ack = new Ack { Seq = first.Seq } }, ct);

        try
        {
            await foreach (var message in requestStream.ReadAllAsync(ct))
            {
                await HandleAsync(agentId, message, ct);
                await responseStream.WriteAsync(new ConnectResponse { Ack = new Ack { Seq = message.Seq } }, ct);
            }
        }
        finally
        {
            logger.LogInformation("Agent {AgentId} disconnected", agentId);
        }
    }

    private async Task HandleAsync(Guid agentId, ConnectRequest message, CancellationToken ct)
    {
        switch (message.PayloadCase)
        {
            case ConnectRequest.PayloadOneofCase.Metrics:
                await metrics.WriteAsync(agentId, message.Metrics, ct);
                break;

            case ConnectRequest.PayloadOneofCase.Heartbeat:
                await TouchAsync(agentId, ct);
                break;

            case ConnectRequest.PayloadOneofCase.Inventory:
                await SaveInventoryAsync(agentId, message.Inventory, ct);
                break;

            default:
                // Логи, изменения и события — неделя 5.
                logger.LogDebug("Agent {AgentId}: {Payload} not handled yet", agentId, message.PayloadCase);
                break;
        }
    }

    private async Task TouchAsync(Guid agentId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IccDbContext>();
        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, time.GetUtcNow()), ct);
    }

    private async Task SaveInventoryAsync(Guid agentId, InventorySnapshot snapshot, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IccDbContext>();
        var json = InventoryJson.Format(snapshot);
        var collectedAt = snapshot.CollectedAt?.ToDateTimeOffset() ?? time.GetUtcNow();

        var existing = await db.AgentInventories.SingleOrDefaultAsync(i => i.AgentId == agentId, ct);
        if (existing is null)
            db.AgentInventories.Add(new AgentInventory { AgentId = agentId, CollectedAt = collectedAt, Snapshot = json });
        else
        {
            existing.CollectedAt = collectedAt;
            existing.Snapshot = json;
        }
        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, time.GetUtcNow()), ct);
        await db.SaveChangesAsync(ct);
    }

    internal static AgentConfig DefaultConfig() => new()
    {
        HeartbeatIntervalSeconds = 10,
        MetricsIntervalSeconds = 5,
        InventoryIntervalSeconds = 30,
        MaxLogLineBytes = 16 * 1024,
    };

    private sealed record X509CertificateInfo(string Pem, string Serial, DateTimeOffset NotAfter);
}
