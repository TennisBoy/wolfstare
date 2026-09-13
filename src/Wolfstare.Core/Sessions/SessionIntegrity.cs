using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// HMAC-SHA256 over an active session's state (spec §6), so a hand-edit of the database is
/// detected. It is re-computed and stored on every session write, so it always covers the
/// current state — including the live elapsed count, which an attacker could otherwise inflate
/// to force a timed lock to look expired.
///
/// This is a speed bump, honestly labelled: the key is machine-DPAPI-protected, and an
/// administrator running as SYSTEM can unprotect it. It defeats a text editor, not a determined
/// attacker (spec §2.3). Detection fails closed — <see cref="Verify"/> returns false, never
/// throws, on any malformed input, and the caller keeps the block on.
/// </summary>
public static class SessionIntegrity
{
    public static string Sign(BlockSession session, ReadOnlySpan<byte> key)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(CanonicalState(session)));
        return Convert.ToBase64String(mac);
    }

    public static bool Verify(BlockSession session, string mac, ReadOnlySpan<byte> key)
    {
        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(mac);
        }
        catch (FormatException)
        {
            return false; // not even base64 — treat as tampered
        }

        if (provided.Length != HMACSHA256.HashSizeInBytes) return false;

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(CanonicalState(session)));
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    /// <summary>
    /// A stable string over every field that must not be forged. Order and delimiters are fixed
    /// so the same session always produces the same bytes. A lock's password hash is included so
    /// swapping in an attacker-known password is caught.
    /// </summary>
    private static string CanonicalState(BlockSession session)
    {
        var (lockKind, lockSecret) = session.Lock switch
        {
            NoLock => ("none", ""),
            TimedLock => ("timed", ""),
            PasswordLock l => ("password", $"{Convert.ToBase64String(l.Hash.Hash)}:{Convert.ToBase64String(l.Hash.Salt)}:{l.Hash.Iterations}"),
            // The required text is signed, so editing the database to a shorter string you'd
            // rather type is caught.
            RandomTextLock l => ("randomtext", l.RequiredText),
            _ => ("unknown", ""),
        };

        var t = session.Timing;
        return string.Join('|',
            "wolfstare-session-v1",
            session.Id.ToString("N"),
            session.BlockListId.ToString("N"),
            lockKind,
            lockSecret,
            session.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? "null",
            t.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            t.ElapsedSeconds.ToString(CultureInfo.InvariantCulture),
            t.CheckpointWallUtc.ToString("O", CultureInfo.InvariantCulture),
            t.CheckpointMonotonicMs.ToString(CultureInfo.InvariantCulture));
    }
}
