namespace Wolfstare.Contracts;

/// <summary>
/// The wire shapes for the local HTTP API (spec §10). Kept separate from the domain so the
/// public contract cannot drift as an incidental consequence of a domain refactor, and so
/// TypeScript types can be generated from one place.
/// </summary>
public sealed record RuleDto(string Kind, string Value);

public sealed record BlockListDto(
    Guid Id,
    string Name,
    IReadOnlyList<RuleDto> Rules,
    IReadOnlyList<RuleDto> Allowlist);

public sealed record CreateBlockListRequest(
    string Name,
    IReadOnlyList<RuleDto> Rules,
    IReadOnlyList<RuleDto>? Allowlist);

/// <summary>
/// How to lock a session. <c>Kind</c> is one of "none", "password", "timed", or "randomtext".
/// A password is required for "password"; a duration for "timed"; <c>TextLength</c> sets the
/// number of characters to retype for "randomtext" (default 5000).
/// </summary>
public sealed record LockDto(string Kind, string? Password, int? TextLength = null);

public sealed record StartSessionRequest(long? DurationMinutes, LockDto Lock);

public sealed record StopSessionRequest(string? Password);

public sealed record UnlockRequest(Guid BlockListId, string Password);

public sealed record ActiveSessionDto(
    Guid Id,
    Guid BlockListId,
    string BlockListName,
    string LockKind,
    long? RemainingSeconds,
    long ElapsedSeconds,
    bool CanBeStopped,
    // For a random-text lock, how many characters must be retyped to stop it. The text itself is
    // never returned as a string — it is served only as an image (GET /api/blocklists/{id}/unlock-image)
    // so it cannot be scraped and posted back by a script.
    int? UnlockTextLength = null);

public sealed record StatusDto(
    IReadOnlyList<ActiveSessionDto> ActiveSessions,
    string Health);

public sealed record ErrorDto(string Message);
