namespace Wolfstare.Core.Sessions;

/// <summary>
/// How a session is protected from being stopped. Closed hierarchy — see spec §4.3.
/// </summary>
public abstract record SessionLock;

/// <summary>No protection. The session can be stopped at any time.</summary>
public sealed record NoLock : SessionLock;

/// <summary>Stopping requires the password.</summary>
public sealed record PasswordLock(PasswordHash Hash) : SessionLock;

/// <summary>
/// Stopping is impossible until the session's duration elapses.
///
/// There is deliberately no member here — no password, no override, no escape hatch. The
/// absence is the feature: <see cref="StopPolicy"/> has no branch that ends a timed session
/// before expiry, so no caller can request one and no future caller can be given one without
/// that removal being visible in a diff.
/// </summary>
public sealed record TimedLock : SessionLock;
