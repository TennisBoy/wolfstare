# Wolfstare Phase 2: Sessions, Persistence, and HTTP API

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Turn the phase 1 domain library into a runnable, curl-able service: block lists that persist, a session manager that accrues time and enforces `StopPolicy`, and the HTTP API from spec §10.

**Architecture:** `Wolfstare.Core` gains `BlockList` and `SessionManager` — still pure, still no Windows references. `Wolfstare.Service` hosts an ASP.NET Core Minimal API and owns persistence behind repository interfaces Core defines. Enforcement stays stubbed; phase 3 supplies the real adapters.

**Tech Stack:** .NET 9, ASP.NET Core Minimal API, `Microsoft.Data.Sqlite`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-27-wolfstare-design.md`

## Global Constraints

- Everything from phase 1 still applies: `Wolfstare.Core` stays `net9.0` with zero package references.
- `SessionManager` is the only thing that mutates session state, and it always routes stop requests through `StopPolicy`.
- The API returns `423 Locked` identically regardless of caller (spec §2.1).
- Time accrual happens in exactly one place. No endpoint recomputes elapsed time.
- Fail closed: a repository that cannot read state leaves existing enforcement in place.

---

### Task 1: `BlockList` and the repository interfaces

**Files:**
- Create: `src/Wolfstare.Core/Rules/BlockList.cs`
- Create: `src/Wolfstare.Core/Storage/IBlockListRepository.cs`
- Create: `src/Wolfstare.Core/Storage/ISessionRepository.cs`
- Test: `tests/Wolfstare.Core.Tests/Rules/BlockListTests.cs`

**Interfaces:**
- Consumes: `BlockRule`, `RuleSet`, `RuleValidator` (phase 1)
- Produces:
  - `sealed record BlockList(Guid Id, string Name, IReadOnlyList<BlockRule> Rules, IReadOnlyList<BlockRule> Allowlist)` with `RuleSet ToRuleSet()` and `static ValidationResult Validate(BlockList)`
  - `interface IBlockListRepository { Task<IReadOnlyList<BlockList>> GetAllAsync(CancellationToken); Task<BlockList?> GetAsync(Guid, CancellationToken); Task SaveAsync(BlockList, CancellationToken); Task DeleteAsync(Guid, CancellationToken); }`
  - `interface ISessionRepository { Task<IReadOnlyList<BlockSession>> GetActiveAsync(CancellationToken); Task SaveAsync(BlockSession, CancellationToken); Task RemoveAsync(Guid, CancellationToken); }`

- [x] **Step 1: Write failing tests** covering: `ToRuleSet` round-trips rules and allowlist; `Validate` rejects an empty name; `Validate` rejects a list containing any rule that `RuleValidator` rejects and surfaces that rule's message; `Validate` accepts a well-formed list.
- [x] **Step 2: Run** `dotnet test --filter BlockListTests` — expect FAIL.
- [x] **Step 3: Implement** `BlockList` (validation delegates to `RuleValidator` per rule, returning the first failure) and the two repository interfaces.
- [x] **Step 4: Run** — expect PASS.
- [x] **Step 5: Commit.**

---

### Task 2: `SessionManager`

The orchestrator. Owns active sessions, accrues time, and is the only caller of `StopPolicy`.

**Files:**
- Create: `src/Wolfstare.Core/Sessions/SessionManager.cs`
- Create: `src/Wolfstare.Core/Sessions/SessionStartRequest.cs`
- Test: `tests/Wolfstare.Core.Tests/Sessions/SessionManagerTests.cs`
- Test: `tests/Wolfstare.Core.Tests/Storage/InMemoryRepositories.cs`

**Interfaces:**
- Consumes: `BlockSession`, `StopPolicy`, `IPasswordHasher`, `IClock`, `ElapsedCalculator`, both repositories
- Produces:
  - `sealed record SessionStartRequest(Guid BlockListId, long? DurationSeconds, SessionLock Lock)`
  - `sealed class SessionManager` with `Task<StartResult> StartAsync(...)`, `Task<StopOutcome> StopAsync(Guid blockListId, string? password, ...)`, `Task TickAsync(...)`, `Task<IReadOnlyList<BlockSession>> GetActiveAsync(...)`, `Task<RuleSet> GetEffectiveRuleSetAsync(...)`
  - `enum StartFailure { None, BlockListNotFound, AlreadyActive, TimedLockNeedsDuration }`
  - `readonly record struct StartResult(BlockSession? Session, StartFailure Failure)`

- [x] **Step 1: Write `InMemoryRepositories`** — dictionary-backed fakes for both interfaces.
- [x] **Step 2: Write failing tests.** Required cases:
  - Starting a session for an unknown block list fails with `BlockListNotFound`.
  - Starting a second session for an already-active list fails with `AlreadyActive`.
  - A `TimedLock` with a null duration fails with `TimedLockNeedsDuration` — an indefinite timed lock would be unstoppable forever, which is a footgun, not a feature.
  - `TickAsync` accrues time via `ElapsedCalculator` and persists the checkpoint.
  - `TickAsync` removes a session once expired.
  - `StopAsync` on a timed session returns `Locked` and leaves the session active.
  - `StopAsync` on a password session with the wrong password returns `PasswordIncorrect` and leaves it active.
  - `StopAsync` with the correct password removes the session.
  - `GetEffectiveRuleSetAsync` unions the rules of all active sessions, and unions their allowlists.
  - A session that is active but whose block list was deleted contributes nothing and does not throw.
- [x] **Step 3: Run** — expect FAIL.
- [x] **Step 4: Implement `SessionManager`.** All stop decisions delegate to `StopPolicy`; the manager never re-derives lock semantics.
- [x] **Step 5: Run** — expect PASS.
- [x] **Step 6: Commit.**

---

### Task 3: SQLite persistence

**Files:**
- Create: `src/Wolfstare.Service/Wolfstare.Service.csproj` (ASP.NET Core, `net9.0-windows`)
- Create: `src/Wolfstare.Service/Storage/SqliteConnectionFactory.cs`
- Create: `src/Wolfstare.Service/Storage/SchemaInitialiser.cs`
- Create: `src/Wolfstare.Service/Storage/SqliteBlockListRepository.cs`
- Create: `src/Wolfstare.Service/Storage/SqliteSessionRepository.cs`
- Create: `src/Wolfstare.Service/Storage/RuleJson.cs`
- Test: `tests/Wolfstare.Service.Tests/Storage/SqliteRepositoryTests.cs`

Rules serialise as JSON with a `kind` discriminator (`domain`, `app.imageName`, `app.publisher`, `app.fileDescription`, `path`) so the closed hierarchies survive a round trip. Tests run against a temp-file database, created and deleted per test.

- [x] **Step 1: Write failing round-trip tests** — save and reload a block list with every rule kind; save and reload a session with each lock kind including the password hash bytes; delete removes; unknown discriminator fails closed by throwing at load rather than silently dropping the rule.
- [x] **Step 2: Run** — expect FAIL.
- [x] **Step 3: Implement** schema, JSON converters, and both repositories. WAL mode on.
- [x] **Step 4: Run** — expect PASS.
- [x] **Step 5: Commit.**

---

### Task 4: HTTP API

**Files:**
- Create: `src/Wolfstare.Contracts/Wolfstare.Contracts.csproj` + DTOs
- Create: `src/Wolfstare.Service/Api/BlockListEndpoints.cs`
- Create: `src/Wolfstare.Service/Api/SessionEndpoints.cs`
- Create: `src/Wolfstare.Service/Api/TokenAuth.cs`
- Create: `src/Wolfstare.Service/Program.cs`
- Test: `tests/Wolfstare.Service.Tests/Api/ApiTests.cs` (via `WebApplicationFactory`)

Endpoints per spec §10.1. `TokenAuth` validates the bearer token and the `Origin`/`Host` headers.

- [x] **Step 1: Write failing endpoint tests.** Required cases:
  - `GET /api/status` with no active session returns an empty list and healthy status.
  - `POST /api/blocklists` with an invalid rule returns 400 carrying the validator's message.
  - `POST /api/blocklists/{id}/start` with a timed lock returns 200 and the session appears in status.
  - `POST /api/blocklists/{id}/stop` on a timed session returns **423** with remaining seconds.
  - `PUT` and `DELETE` on a block list with an active timed session return **423**.
  - `POST /api/unlock` with the correct password on a password-locked session returns 200.
  - `POST /api/unlock` with a wrong password returns 401.
  - `POST /api/unlock` on a timed session returns 423 even with a password.
  - A request with no bearer token returns 401.
  - A request with a cross-origin `Origin` header returns 403.
- [x] **Step 2: Run** — expect FAIL.
- [x] **Step 3: Implement** DTOs, endpoints, token auth, and `Program.cs` with a `--console` switch.
- [x] **Step 4: Run** — expect PASS.
- [x] **Step 5: Commit.**

---

## Phase 2 Definition of Done

- `dotnet test` green across Core and Service suites.
- The service runs with `dotnet run --project src/Wolfstare.Service -- --console` and answers `GET /api/status`.
- A timed session cannot be stopped through any endpoint, and a test asserts this for `stop`, `unlock`, `PUT`, and `DELETE`.
- Block lists and sessions survive a service restart.

---

## Execution record

Completed 2026-08-27. **152 tests passing** (121 Core, 31 Service).

Verified against a running service, not only in tests: a 25-minute timed lock returned 423 to
both `stop` and `DELETE`, `explorer.exe` was refused with the validator's message, and the
session survived `taskkill /F` with 11 seconds of downtime correctly credited on resume.

### Deviations

1. **`Wolfstare.Service` targets `net9.0`, not `net9.0-windows`.** Nothing in phase 2 touches
   a Windows API, and the portable TFM keeps the test suite runnable anywhere. Phase 3 will
   need the Windows TFM for registry, WMI, and ETW — switch it then.

2. **Package versions pinned to the 9.x line.** `dotnet add package` resolves
   `Microsoft.Data.Sqlite` and `Microsoft.AspNetCore.Mvc.Testing` to 10.x by default, which is
   incompatible with `net9.0`. Both are pinned to 9.0.19.

3. **`PUT`/`DELETE` are refused for password locks too, not only timed ones.** The plan said
   "active timed session"; the implementation refuses any non-`NoLock` session. Editing a list
   out from under a password lock would weaken the block without ever presenting the password,
   which is a bypass. The caller can `stop` with the password first, then edit freely.

4. **`StopAsync` accrues time before deciding.** A session that has just expired should be
   stoppable without the caller having to tick first.

5. **`ResumeAsync` added to `SessionManager`**, alongside `TickAsync`. The plan implied resume
   but did not name it; `SessionTicker` calls it once at startup.

6. **`--console` was not needed as an explicit switch.** The service is a plain
   `WebApplication`, so `dotnet run` already gives console hosting. The switch becomes
   meaningful in phase 3, when Windows Service hosting is added and console mode has to be
   selected explicitly.

### Not yet done from spec §10

`GET /api/events` (WebSocket) and the CLI project are deferred — the REST surface covers
everything the UI needs to be built against, and live events are better designed alongside the
enforcement layer that will raise them.
