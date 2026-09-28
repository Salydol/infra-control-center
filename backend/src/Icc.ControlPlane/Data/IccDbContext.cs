using Microsoft.EntityFrameworkCore;

namespace Icc.ControlPlane.Data;

public sealed class IccDbContext(DbContextOptions<IccDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<JoinToken> JoinTokens => Set<JoinToken>();
    public DbSet<AgentInventory> AgentInventories => Set<AgentInventory>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.HasKey(a => a.Id);
            e.Property(a => a.Hostname).HasMaxLength(255);
            e.Property(a => a.AgentVersion).HasMaxLength(64);
            e.Property(a => a.CertificateSerial).HasMaxLength(64);
            e.HasIndex(a => a.Hostname);
        });

        model.Entity<JoinToken>(e =>
        {
            e.ToTable("join_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(16);
            e.Property(t => t.SecretHash).HasMaxLength(64);
            e.Property(t => t.Description).HasMaxLength(255);
        });

        model.Entity<AgentInventory>(e =>
        {
            e.ToTable("agent_inventories");
            e.HasKey(i => i.AgentId);
            e.Property(i => i.Snapshot).HasColumnType("jsonb");
            e.HasOne<Agent>().WithOne().HasForeignKey<AgentInventory>(i => i.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class Agent
{
    public Guid Id { get; set; }
    public required string Hostname { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public required string CertificateSerial { get; set; }
    public DateTimeOffset CertificateNotAfter { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>Одноразовый токен регистрации агента. Хранится только хеш секрета.</summary>
public sealed class JoinToken
{
    public required string Id { get; set; }
    public required string SecretHash { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByAgentId { get; set; }
}

/// <summary>Последний снимок инвентаря агента (контейнеры, compose-проекты, хост) в JSON.</summary>
public sealed class AgentInventory
{
    public Guid AgentId { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public required string Snapshot { get; set; }
}
