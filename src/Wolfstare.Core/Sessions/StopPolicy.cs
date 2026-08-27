using Wolfstare.Core.Time;

namespace Wolfstare.Core.Sessions;

/// <summary>The result of asking whether a session may be stopped.</summary>
public enum StopOutcome
{
    /// <summary>The session may be stopped now.</summary>
    Allowed,

    /// <summary>A password is required and none was supplied.</summary>
    PasswordRequired,

    /// <summary>A password was supplied and it was wrong.</summary>
    PasswordIncorrect,

    /// <summary>The session is locked and nothing the caller can do will change that.</summary>
    Locked,
}

/// <summary>
/// The single authority on whether a session may end early (spec §2.1: authority is policy,
/// not transport).
///
/// Every caller — HTTP API, CLI, internal scheduler — routes through here, which is why it
/// does not matter that the local API is reachable by any process on the machine. There is
/// no privileged path that bypasses this method, so there is nothing to gain by finding one.
/// </summary>
public static class StopPolicy
{
    public static StopOutcome CanStop(
        BlockSession session, string? password, IPasswordHasher hasher, IClock clock)
    {
        // An expired session is over, however it was locked.
        if (session.IsExpired()) return StopOutcome.Allowed;

        return session.Lock switch
        {
            NoLock => StopOutcome.Allowed,

            // Note the absence of a password branch. A timed lock has no early exit; adding
            // one here would silently remove the only guarantee this application makes.
            TimedLock => StopOutcome.Locked,

            PasswordLock when password is null => StopOutcome.PasswordRequired,
            PasswordLock l => hasher.Verify(l.Hash, password)
                ? StopOutcome.Allowed
                : StopOutcome.PasswordIncorrect,

            // Fail closed on an unrecognised lock type rather than defaulting to open.
            _ => StopOutcome.Locked,
        };
    }
}
