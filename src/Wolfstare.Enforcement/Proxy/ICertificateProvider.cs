namespace Wolfstare.Enforcement.Proxy;

/// <summary>
/// The seam for HTTPS inspection (spec §7.3). Enabling path-level rules later means supplying
/// a provider backed by a locally trusted CA — a registration change, not a proxy rewrite.
/// </summary>
public interface ICertificateProvider
{
    /// <summary>True when the proxy may terminate TLS and see request paths.</summary>
    bool CanInspect { get; }
}

/// <summary>The v1 provider: no CA, no decryption, domain-level decisions only.</summary>
public sealed class NullCertificateProvider : ICertificateProvider
{
    public bool CanInspect => false;
}
