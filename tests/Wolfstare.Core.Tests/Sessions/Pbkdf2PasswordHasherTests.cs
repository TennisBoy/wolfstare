using Wolfstare.Core.Sessions;

namespace Wolfstare.Core.Tests.Sessions;

public class Pbkdf2PasswordHasherTests
{
    // 600,000 iterations is deliberately slow, so this suite creates few hashes.
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void CorrectPasswordVerifies()
    {
        var stored = _hasher.Create("correct horse battery staple");

        Assert.True(_hasher.Verify(stored, "correct horse battery staple"));
    }

    [Fact]
    public void IncorrectPasswordDoesNotVerify()
    {
        var stored = _hasher.Create("correct horse battery staple");

        Assert.False(_hasher.Verify(stored, "Correct horse battery staple"));
        Assert.False(_hasher.Verify(stored, ""));
    }

    [Fact]
    public void SaltIsRandomPerHash()
    {
        var a = _hasher.Create("same password");
        var b = _hasher.Create("same password");

        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.True(_hasher.Verify(a, "same password"));
        Assert.True(_hasher.Verify(b, "same password"));
    }

    [Fact]
    public void HashUsesSpecifiedParameters()
    {
        var stored = _hasher.Create("x");

        Assert.Equal(600_000, stored.Iterations);
        Assert.Equal(16, stored.Salt.Length);   // 128-bit salt
        Assert.Equal(32, stored.Hash.Length);   // 256-bit derived key
    }

    [Fact]
    public void EmptyPasswordIsRejectedAtCreation()
        => Assert.Throws<ArgumentException>(() => _hasher.Create("  "));

    [Fact]
    public void CorruptStoredHashFailsClosed()
    {
        var stored = _hasher.Create("password");
        var corrupt = stored with { Hash = [1, 2, 3] };

        Assert.False(_hasher.Verify(corrupt, "password"));
    }

    [Fact]
    public void EmptyStoredFieldsFailClosed()
    {
        Assert.False(_hasher.Verify(new PasswordHash([], [], 0), "password"));
        Assert.False(_hasher.Verify(new PasswordHash([1], [], 600_000), "password"));
    }
}
