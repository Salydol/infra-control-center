using Grpc.Core;

namespace Icc.ControlPlane.Agents;

/// <summary>Определяет агента по клиентскому сертификату соединения (mTLS).</summary>
public interface IAgentIdentity
{
    Guid? Resolve(ServerCallContext context);
}

public sealed class CertificateAgentIdentity(AgentPki pki) : IAgentIdentity
{
    public Guid? Resolve(ServerCallContext context)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null || !pki.ValidateAgentCertificate(certificate))
            return null;
        return AgentPki.AgentIdOf(certificate);
    }
}
