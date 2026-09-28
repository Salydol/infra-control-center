using System.Security.Cryptography;
using System.Text;
using Icc.ControlPlane.Data;
using Microsoft.EntityFrameworkCore;

namespace Icc.ControlPlane.Agents;

/// <summary>
/// Токен регистрации: <c>icc1.&lt;id&gt;.&lt;secret&gt;.&lt;sha256 CA&gt;</c>.
/// Секрет подтверждает право зарегистрироваться (одноразово), хеш CA позволяет
/// агенту проверить, что он говорит именно с этим центром, ещё до регистрации.
/// </summary>
public sealed record ParsedJoinToken(string Id, string Secret, string CaHash)
{
    public const string Prefix = "icc1";

    public static ParsedJoinToken? Parse(string token)
    {
        var parts = token.Trim().Split('.');
        if (parts.Length != 4 || parts[0] != Prefix || parts[1].Length != 8 || parts[2].Length != 32
            || parts[3].Length != 64)
            return null;
        return new ParsedJoinToken(parts[1], parts[2], parts[3]);
    }

    public override string ToString() => $"{Prefix}.{Id}.{Secret}.{CaHash}";
}

public sealed class JoinTokenService(IccDbContext db, AgentPki pki, TimeProvider time)
{
    public async Task<string> CreateAsync(TimeSpan ttl, string? description, CancellationToken ct)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var now = time.GetUtcNow();
        db.JoinTokens.Add(new JoinToken
        {
            Id = id,
            SecretHash = Hash(secret),
            Description = description,
            CreatedAt = now,
            ExpiresAt = now + ttl,
        });
        await db.SaveChangesAsync(ct);
        return new ParsedJoinToken(id, secret, pki.CaHash).ToString();
    }

    /// <summary>Проверяет и «гасит» токен. Возвращает null, если токен недействителен.</summary>
    public async Task<JoinToken?> RedeemAsync(string token, Guid agentId, CancellationToken ct)
    {
        var parsed = ParsedJoinToken.Parse(token);
        if (parsed is null || parsed.CaHash != pki.CaHash)
            return null;

        var entity = await db.JoinTokens.SingleOrDefaultAsync(t => t.Id == parsed.Id, ct);
        if (entity is null || entity.UsedAt is not null || entity.ExpiresAt < time.GetUtcNow())
            return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(entity.SecretHash), Convert.FromHexString(Hash(parsed.Secret))))
            return null;

        entity.UsedAt = time.GetUtcNow();
        entity.UsedByAgentId = agentId;
        return entity;
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
