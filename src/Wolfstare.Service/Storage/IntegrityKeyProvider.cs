using System.Security.Cryptography;

namespace Wolfstare.Service.Storage;

/// <summary>
/// Supplies the 256-bit key that signs session integrity MACs (spec §6). The key is generated
/// once and stored DPAPI-protected at machine scope, so it is bound to this machine and not
/// readable as plaintext from the file.
///
/// This is a speed bump, not a vault: an administrator running as SYSTEM can unprotect it. It
/// stops a text-editor edit of the database, which is the realistic tamper attempt (spec §2.3).
/// </summary>
public sealed class IntegrityKeyProvider
{
    private static readonly byte[] Entropy = "Wolfstare.SessionIntegrity.v1"u8.ToArray();

    private readonly Lazy<byte[]> _key;

    public IntegrityKeyProvider(WolfstarePaths paths)
        => _key = new Lazy<byte[]>(() => LoadOrCreate(Path.Combine(paths.RootDirectory, "integrity.key")));

    public byte[] Key => _key.Value;

    private static byte[] LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException)
            {
                // Unreadable (copied from another machine, or corrupt). Regenerate — the worst
                // case is that existing MACs no longer verify, which fails closed: blocks stay on.
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(path, protectedKey);
        return key;
    }
}
