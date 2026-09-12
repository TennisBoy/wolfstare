using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Apps;

/// <summary>
/// Reads an executable's identity from disk — image name, Authenticode publisher, and version
/// description. This is what lets the ETW watcher recognise a renamed executable: the file's
/// signer does not change when the file is renamed (spec §8.2).
///
/// Every lookup fails soft to null. A process may exit, be inaccessible, or be unsigned before
/// we finish inspecting it, and none of that should throw on the watcher's hot path.
/// </summary>
public static class ProcessInspector
{
    public static ProcessIdentity FromImagePath(string imagePath)
        => new(imagePath, PublisherOf(imagePath), DescriptionOf(imagePath));

    /// <summary>The Authenticode certificate subject, or null when unsigned or unreadable.</summary>
    public static string? PublisherOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // Extracts the Authenticode signer's certificate from a signed PE file. Throws
            // CryptographicException for an unsigned file — the common, non-error case.
            //
            // SYSLIB0057 steers away from the loading constructors toward X509CertificateLoader,
            // but that loader reads certificate *files* and has no equivalent for pulling an
            // embedded Authenticode signer out of an executable. This remains the API for the
            // job, so the obsoletion is suppressed narrowly here.
#pragma warning disable SYSLIB0057
            var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Unsigned files throw CryptographicException; that is the common case, not an error.
            return null;
        }
    }

    /// <summary>The Win32 version-info FileDescription, or null when absent or unreadable.</summary>
    public static string? DescriptionOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
