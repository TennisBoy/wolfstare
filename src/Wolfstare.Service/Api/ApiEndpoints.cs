using Microsoft.Extensions.Logging;
using Wolfstare.Contracts;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Storage;

namespace Wolfstare.Service.Api;

/// <summary>
/// The local HTTP API (spec §10.1).
///
/// Every mutation that could weaken an active block routes through <see cref="SessionManager"/>
/// and therefore through <c>StopPolicy</c>. There is no endpoint that ends a timed session, and
/// no combination of endpoints that adds up to one — editing or deleting a block list with an
/// active locked session answers 423 for the same reason stopping it does.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>The allowed range for a random-text lock's length, in characters.</summary>
    public const int RandomTextLockMinLength = 100;
    public const int RandomTextLockMaxLength = 5000;
    private const int RandomTextLockDefaultLength = 500;

    public static void MapWolfstareApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", GetStatus);
        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapGet("/blocklists", GetBlockLists);
        api.MapGet("/blocklists/{id:guid}", GetBlockList);
        api.MapPost("/blocklists", CreateBlockList);
        api.MapPut("/blocklists/{id:guid}", UpdateBlockList);
        api.MapDelete("/blocklists/{id:guid}", DeleteBlockList);

        api.MapPost("/blocklists/{id:guid}/start", StartSession);
        api.MapPost("/blocklists/{id:guid}/stop", StopSession);
        api.MapGet("/blocklists/{id:guid}/unlock-image", GetUnlockImage);
        api.MapPost("/unlock", Unlock);
    }

    private static async Task<IResult> GetStatus(
        SessionManager manager,
        IBlockListRepository lists,
        Wolfstare.Service.Enforcement.EnforcementHealth health,
        CancellationToken ct)
    {
        var active = await manager.GetActiveAsync(ct);
        var dtos = new List<ActiveSessionDto>(active.Count);

        foreach (var session in active)
        {
            var list = await lists.GetAsync(session.BlockListId, ct);

            dtos.Add(new ActiveSessionDto(
                session.Id,
                session.BlockListId,
                list?.Name ?? "(deleted)",
                LockKindOf(session.Lock),
                session.RemainingSeconds(),
                session.Timing.ElapsedSeconds,
                CanBeStopped: session.Lock is NoLock || session.IsExpired(),
                UnlockTextLength: session.Lock is RandomTextLock r ? r.RequiredText.Length : null));
        }

        // Health reflects the enforcement subsystems: "ok", or "degraded" with a reason the UI
        // can show — a degraded blocker is still blocking, but less completely than the user
        // may assume, so it must be surfaced rather than hidden.
        var status = health.Status == Wolfstare.Service.Enforcement.EnforcementStatus.Ok ? "ok" : "degraded";
        return Results.Ok(new StatusDto(dtos, status));
    }

    private static async Task<IResult> GetBlockLists(IBlockListRepository lists, CancellationToken ct)
        => Results.Ok((await lists.GetAllAsync(ct)).Select(RuleMapping.ToDto).ToList());

    private static async Task<IResult> GetBlockList(
        Guid id, IBlockListRepository lists, CancellationToken ct)
    {
        var list = await lists.GetAsync(id, ct);
        return list is null ? Results.NotFound() : Results.Ok(RuleMapping.ToDto(list));
    }

    private static async Task<IResult> CreateBlockList(
        CreateBlockListRequest request, IBlockListRepository lists, CancellationToken ct)
    {
        var built = Build(Guid.NewGuid(), request.Name, request.Rules, request.Allowlist);
        if (built.Error is not null) return Results.BadRequest(new ErrorDto(built.Error));

        await lists.SaveAsync(built.List!, ct);
        return Results.Created($"/api/blocklists/{built.List!.Id}", RuleMapping.ToDto(built.List));
    }

    private static async Task<IResult> UpdateBlockList(
        Guid id,
        CreateBlockListRequest request,
        IBlockListRepository lists,
        SessionManager manager,
        CancellationToken ct)
    {
        if (await lists.GetAsync(id, ct) is null) return Results.NotFound();

        if (await LockedSessionFor(id, manager, ct) is { } locked)
            return LockedResult(locked);

        var built = Build(id, request.Name, request.Rules, request.Allowlist);
        if (built.Error is not null) return Results.BadRequest(new ErrorDto(built.Error));

        await lists.SaveAsync(built.List!, ct);
        return Results.Ok(RuleMapping.ToDto(built.List!));
    }

    private static async Task<IResult> DeleteBlockList(
        Guid id, IBlockListRepository lists, SessionManager manager, CancellationToken ct)
    {
        if (await lists.GetAsync(id, ct) is null) return Results.NotFound();

        if (await LockedSessionFor(id, manager, ct) is { } locked)
            return LockedResult(locked);

        await lists.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> StartSession(
        Guid id,
        StartSessionRequest request,
        SessionManager manager,
        Wolfstare.Service.Enforcement.IEnforcementRefresh refresh,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Only random-text locks may be created. The easier lock kinds (none / password /
        // timed) are deliberately refused — they are the "back doors" a weaker block would
        // offer, so a block can only ever be escaped by retyping the string. The domain still
        // understands the other kinds (for existing sessions and the tests that cover their
        // semantics); they simply cannot be started here.
        if (request.Lock.Kind != "randomtext")
            return Results.BadRequest(new ErrorDto(
                "Only random-text locks are allowed. There is no weaker lock to fall back on — "
                + "a block can only be ended by retyping its text."));

        var length = request.Lock.TextLength ?? RandomTextLockDefaultLength;
        if (length < RandomTextLockMinLength || length > RandomTextLockMaxLength)
            return Results.BadRequest(new ErrorDto(
                $"Random-text length must be between {RandomTextLockMinLength} and {RandomTextLockMaxLength} characters."));

        SessionLock sessionLock = new RandomTextLock(RandomText.Generate(length));

        var duration = request.DurationMinutes is { } minutes ? minutes * 60 : (long?)null;
        if (duration is <= 0)
            return Results.BadRequest(new ErrorDto("Duration must be greater than zero."));

        var result = await manager.StartAsync(new SessionStartRequest(id, duration, sessionLock), ct);

        if (result.Failure == StartFailure.None)
            await SafeRefreshAsync(refresh, loggerFactory, ct);

        return result.Failure switch
        {
            StartFailure.None => Results.Ok(),
            StartFailure.BlockListNotFound => Results.NotFound(),
            StartFailure.AlreadyActive => Results.Conflict(
                new ErrorDto("That block list already has an active session.")),
            StartFailure.TimedLockNeedsDuration => Results.BadRequest(
                new ErrorDto("A timed lock needs a duration, otherwise it could never end.")),
            _ => Results.BadRequest(new ErrorDto("Could not start the session.")),
        };
    }

    /// <summary>
    /// Serves the random-text unlock string as a PNG. The text is never returned as a string by
    /// any endpoint — rendering it as pixels is what stops a script reading it and posting it
    /// back. Requires the bearer token like everything else under /api.
    /// </summary>
    private static async Task<IResult> GetUnlockImage(Guid id, SessionManager manager, CancellationToken ct)
    {
        var session = (await manager.GetActiveAsync(ct)).FirstOrDefault(s => s.BlockListId == id);
        if (session?.Lock is not RandomTextLock rt)
            return Results.NotFound();

        if (!OperatingSystem.IsWindows())
            return Results.Problem("Image rendering is only available on Windows.");

        return Results.File(UnlockChallengeImage.RenderPng(rt.RequiredText), "image/png");
    }

    private static Task<IResult> StopSession(
        Guid id,
        StopSessionRequest? request,
        SessionManager manager,
        UnlockThrottle throttle,
        Wolfstare.Service.Enforcement.IEnforcementRefresh refresh,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => AttemptStopAsync(id, request?.Password, manager, throttle, refresh, loggerFactory, ct);

    private static Task<IResult> Unlock(
        UnlockRequest request,
        SessionManager manager,
        UnlockThrottle throttle,
        Wolfstare.Service.Enforcement.IEnforcementRefresh refresh,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => AttemptStopAsync(request.BlockListId, request.Password, manager, throttle, refresh, loggerFactory, ct);

    /// <summary>
    /// The one path both <c>/stop</c> and <c>/unlock</c> take, so neither is an unthrottled side
    /// door. For a random-text lock, a wrong retype answers with the position of the first wrong
    /// character and starts a cooldown; an attempt during the cooldown is refused with 429
    /// <em>before</em> it is evaluated, so it reveals nothing — not even that it was right.
    /// The allow/deny decision itself is still <see cref="StopPolicy"/>'s alone.
    /// </summary>
    private static async Task<IResult> AttemptStopAsync(
        Guid blockListId,
        string? password,
        SessionManager manager,
        UnlockThrottle throttle,
        Wolfstare.Service.Enforcement.IEnforcementRefresh refresh,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var session = (await manager.GetActiveAsync(ct)).FirstOrDefault(s => s.BlockListId == blockListId);
        var randomText = session?.Lock as RandomTextLock;

        if (randomText is not null && throttle.Remaining(blockListId) is { } wait)
        {
            return Results.Json(
                new UnlockRefusedDto(
                    $"Wait {CeilingSeconds(wait)} seconds before trying again.", null, CeilingSeconds(wait)),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var outcome = await manager.StopAsync(blockListId, password, ct);
        if (outcome == StopOutcome.Allowed) await SafeRefreshAsync(refresh, loggerFactory, ct);

        if (randomText is not null && outcome == StopOutcome.PasswordIncorrect)
        {
            throttle.RecordFailure(blockListId);
            var index = RandomText.FirstMismatchIndex(randomText.RequiredText, password ?? "");
            return Results.Json(
                new UnlockRefusedDto(
                    "That doesn't match.", index, throttle.Remaining(blockListId) is { } w ? CeilingSeconds(w) : null),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Translate(outcome, await Remaining(blockListId, manager, ct));
    }

    private static int CeilingSeconds(TimeSpan span) => (int)Math.Ceiling(span.TotalSeconds);

    /// <summary>
    /// Kicks enforcement to reconcile immediately after a session change, but never lets that
    /// failing turn an already-committed change into an error to the caller. By the time this
    /// runs the session has already been started or stopped in the domain; the background loop
    /// reconciles enforcement every tick, so a transient failure here (for example tearing down
    /// a hardened IFEO key) is caught up within a refresh interval. Returning 500 instead would
    /// tell the user their unlock failed after it succeeded — and the session is already gone, so
    /// there would be nothing left to retry against.
    /// </summary>
    private static async Task SafeRefreshAsync(
        Wolfstare.Service.Enforcement.IEnforcementRefresh refresh, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            await refresh.RefreshNowAsync(ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Wolfstare.Api.EnforcementRefresh").LogError(
                ex, "Immediate enforcement refresh after a session change failed; the periodic reconcile will retry.");
        }
    }

    /// <summary>
    /// Maps a <see cref="StopOutcome"/> onto its HTTP status. 423 for a lock the caller cannot
    /// satisfy, 401 for a wrong password — the distinction matters to the UI, and neither
    /// leaks anything the caller could not already determine.
    /// </summary>
    private static IResult Translate(StopOutcome outcome, long? remainingSeconds) => outcome switch
    {
        StopOutcome.Allowed => Results.Ok(),
        StopOutcome.PasswordRequired => Results.Json(
            new ErrorDto("This block is password protected."), statusCode: StatusCodes.Status401Unauthorized),
        StopOutcome.PasswordIncorrect => Results.Json(
            new ErrorDto("Incorrect password."), statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.Json(
            new { message = "This block is locked and cannot be stopped.", remainingSeconds },
            statusCode: StatusCodes.Status423Locked),
    };

    private static async Task<long?> Remaining(Guid blockListId, SessionManager manager, CancellationToken ct)
        => (await manager.GetActiveAsync(ct))
            .FirstOrDefault(s => s.BlockListId == blockListId)?.RemainingSeconds();

    /// <summary>
    /// Returns the active session for a list when it is locked against modification.
    ///
    /// Editing or deleting a list under an active lock would weaken the block just as surely
    /// as stopping it, so it is refused the same way. A password-locked session is included:
    /// the caller can supply the password to <c>stop</c>, then edit freely.
    /// </summary>
    private static async Task<BlockSession?> LockedSessionFor(
        Guid blockListId, SessionManager manager, CancellationToken ct)
        => (await manager.GetActiveAsync(ct))
            .FirstOrDefault(s => s.BlockListId == blockListId && s.Lock is not NoLock && !s.IsExpired());

    private static IResult LockedResult(BlockSession session)
        => Results.Json(
            new
            {
                message = "This block list has an active locked session and cannot be modified.",
                remainingSeconds = session.RemainingSeconds(),
            },
            statusCode: StatusCodes.Status423Locked);

    private static (BlockList? List, string? Error) Build(
        Guid id, string name, IReadOnlyList<RuleDto> rules, IReadOnlyList<RuleDto>? allowlist)
    {
        var domainRules = new List<BlockRule>();
        foreach (var dto in rules ?? [])
        {
            var rule = RuleMapping.ToDomain(dto);
            if (rule is null) return (null, $"Unknown rule kind '{dto.Kind}'.");
            domainRules.Add(rule);
        }

        var domainAllowlist = new List<BlockRule>();
        foreach (var dto in allowlist ?? [])
        {
            var rule = RuleMapping.ToDomain(dto);
            if (rule is null) return (null, $"Unknown rule kind '{dto.Kind}'.");
            domainAllowlist.Add(rule);
        }

        var list = new BlockList(id, name, domainRules, domainAllowlist);
        var validation = BlockList.Validate(list);

        return validation.IsValid ? (list, null) : (null, validation.Error);
    }

    private static string LockKindOf(SessionLock sessionLock) => sessionLock switch
    {
        NoLock => "none",
        PasswordLock => "password",
        TimedLock => "timed",
        RandomTextLock => "randomtext",
        _ => "unknown",
    };
}
