using System.Text.Json;
using Icc.ControlPlane.Data;
using Microsoft.EntityFrameworkCore;

namespace Icc.ControlPlane.Agents;

public sealed record AgentView(Guid Id, string Hostname, string? AgentVersion, string Status,
    DateTimeOffset RegisteredAt, DateTimeOffset? LastSeenAt, DateTimeOffset CertificateNotAfter);

public static class AgentEndpoints
{
    /// <summary>Агент считается online, если выходил на связь за последние 30 секунд.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(30);

    public static void MapAgentEndpoints(this WebApplication app)
    {
        // Аутентификация пользователей — неделя 6; пока API доступно без входа.
        var group = app.MapGroup("/api/agents");

        group.MapGet("/", async (IccDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Hostname).ToListAsync(ct);
            return agents.Select(a => ToView(a, now));
        });

        group.MapGet("/{id:guid}", async (Guid id, IccDbContext db, TimeProvider time, CancellationToken ct) =>
            await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } agent
                ? Results.Ok(ToView(agent, time.GetUtcNow()))
                : Results.NotFound());

        group.MapGet("/{id:guid}/inventory", async (Guid id, IccDbContext db, CancellationToken ct) =>
            await db.AgentInventories.AsNoTracking().SingleOrDefaultAsync(i => i.AgentId == id, ct) is { } inventory
                ? Results.Ok(JsonDocument.Parse(inventory.Snapshot).RootElement)
                : Results.NotFound());
    }

    private static AgentView ToView(Agent a, DateTimeOffset now)
    {
        var status = a.Revoked ? "revoked"
            : a.LastSeenAt is { } seen && now - seen < OnlineWindow ? "online"
            : "offline";
        return new AgentView(a.Id, a.Hostname, a.AgentVersion, status, a.RegisteredAt, a.LastSeenAt,
            a.CertificateNotAfter);
    }
}
