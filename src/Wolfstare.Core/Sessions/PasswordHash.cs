namespace Wolfstare.Core.Sessions;

/// <summary>A stored password verifier. Never contains the password itself.</summary>
public sealed record PasswordHash(byte[] Hash, byte[] Salt, int Iterations);

public interface IPasswordHasher
{
    /// <summary>Derives a new hash with a fresh random salt.</summary>
    /// <exception cref="ArgumentException">The password is empty or whitespace.</exception>
    PasswordHash Create(string password);

    /// <summary>
    /// Constant-time verification. Returns false rather than throwing on corrupt input, so
    /// that a damaged record reads as "wrong password" and never as an error a caller might
    /// mistake for success.
    /// </summary>
    bool Verify(PasswordHash stored, string password);
}
