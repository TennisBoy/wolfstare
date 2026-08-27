using System.Security.Cryptography;
using System.Text;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing (spec §4.3).
///
/// The iteration count is high because the threat model assumes an adversary with full local
/// access who can read the database. Against that, the only real defence for a weak password
/// is making each guess expensive.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    public PasswordHash Create(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations, HashBytes);
        return new PasswordHash(hash, salt, Iterations);
    }

    public bool Verify(PasswordHash stored, string password)
    {
        // Fail closed on anything malformed.
        if (stored.Hash.Length == 0 || stored.Salt.Length == 0 || stored.Iterations <= 0)
            return false;
        if (string.IsNullOrEmpty(password))
            return false;

        var candidate = Derive(password, stored.Salt, stored.Iterations, stored.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, stored.Hash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);
}
