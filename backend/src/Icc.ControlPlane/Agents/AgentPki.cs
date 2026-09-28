using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Icc.ControlPlane.Agents;

public sealed class PkiOptions
{
    /// <summary>Каталог с ключами и сертификатами центра (том Docker).</summary>
    public string Directory { get; set; } = "/data/pki";

    /// <summary>Имена и адреса центра для серверного сертификата.</summary>
    public string[] ServerNames { get; set; } = ["localhost", "127.0.0.1", "host.docker.internal", "backend"];

    public int AgentCertificateDays { get; set; } = 90;
}

/// <summary>
/// Собственный CA центра: выпускает серверный сертификат gRPC-endpoint агентов
/// и подписывает сертификаты агентов при регистрации. Создаётся при первом старте.
/// Агент получает CA через GetCenterInfo, сверяет его хеш с токеном регистрации
/// и дальше проверяет сервер по этому CA без проверки имени — центр может жить
/// за любым адресом.
/// </summary>
public sealed class AgentPki
{
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");
    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");

    private readonly PkiOptions _options;

    public X509Certificate2 CaCertificate { get; }
    public X509Certificate2 ServerCertificate { get; }

    /// <summary>SHA-256 от DER сертификата CA (hex) — «пин», который агент получает в токене.</summary>
    public string CaHash { get; }

    public string CaCertificatePem { get; }

    private AgentPki(PkiOptions options, X509Certificate2 ca, X509Certificate2 server)
    {
        _options = options;
        CaCertificate = ca;
        ServerCertificate = server;
        CaHash = Convert.ToHexStringLower(SHA256.HashData(ca.RawData));
        CaCertificatePem = ca.ExportCertificatePem();
    }

    public static AgentPki LoadOrCreate(PkiOptions options)
    {
        System.IO.Directory.CreateDirectory(options.Directory);
        var caCert = Path.Combine(options.Directory, "ca.crt");
        var caKey = Path.Combine(options.Directory, "ca.key");

        X509Certificate2 ca;
        if (File.Exists(caCert) && File.Exists(caKey))
        {
            ca = Reimport(X509Certificate2.CreateFromPem(File.ReadAllText(caCert), File.ReadAllText(caKey)));
        }
        else
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=ICC Agent CA", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllText(caKey, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(caCert, created.ExportCertificatePem());
            TryRestrict(caKey);
            ca = Reimport(created);
        }

        // Серверный сертификат перевыпускается при каждом старте: имена могли измениться.
        var server = IssueServerCertificate(ca, options.ServerNames);
        return new AgentPki(options, ca, server);
    }

    /// <summary>Подписывает CSR агента. Subject задаёт центр: CN = идентификатор агента.</summary>
    public X509Certificate2 SignAgentCsr(string csrPem, Guid agentId)
    {
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={agentId}, O=ICC Agents"),
            csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(CaCertificate, true, false));

        var now = DateTimeOffset.UtcNow;
        return request.Create(CaCertificate, now.AddMinutes(-5), now.AddDays(_options.AgentCertificateDays),
            RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Проверка клиентского сертификата: выпущен нашим CA, действует, предназначен для клиента.</summary>
    public bool ValidateAgentCertificate(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(CaCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(ClientAuth);
        return chain.Build(certificate);
    }

    /// <summary>Идентификатор агента из CN клиентского сертификата.</summary>
    public static Guid? AgentIdOf(X509Certificate2 certificate) =>
        Guid.TryParse(certificate.GetNameInfo(X509NameType.SimpleName, false), out var id) ? id : null;

    private static X509Certificate2 IssueServerCertificate(X509Certificate2 ca, string[] names)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ICC Control Plane", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
                san.AddIpAddress(ip);
            else
                san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], false));

        var now = DateTimeOffset.UtcNow;
        using var issued = request.Create(ca, now.AddMinutes(-5), now.AddDays(365), RandomNumberGenerator.GetBytes(16));
        return Reimport(issued.CopyWithPrivateKey(key));
    }

    /// <summary>
    /// Ключи, созданные в памяти, на Windows не годятся для TLS (SslStream требует
    /// сохранённый ключ). Переимпорт через PKCS#12 решает это на всех ОС.
    /// </summary>
    private static X509Certificate2 Reimport(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.Exportable);

    private static void TryRestrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
