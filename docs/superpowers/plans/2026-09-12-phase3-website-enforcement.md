# Wolfstare Phase 3: Website Enforcement

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make active sessions actually block websites: a DNS sinkhole, a loopback proxy that serves a block page, DoH sealing, and journalled, reversible system changes (spec §7).

**Architecture:** A new `Wolfstare.Enforcement` project (`net9.0-windows`) holds the network servers and the Windows adapters. The servers read the effective rule set from a cache that `SessionManager` state feeds, so no DNS lookup touches SQLite. Every change to machine configuration goes through `ISystemMutator`, which records the prior value before writing so it can be rolled back.

**Tech Stack:** .NET 9, raw `System.Net.Sockets` (no DNS or proxy library), `netsh` for interface DNS and firewall rules, `Microsoft.Win32.Registry` for browser policy and proxy settings.

**Spec:** `docs/superpowers/specs/2026-08-27-wolfstare-design.md`

## Global Constraints

- `Wolfstare.Core` stays `net9.0` with zero package references.
- `Wolfstare.Service` and its tests move to `net9.0-windows` — they must reference `Wolfstare.Enforcement`, and a `net9.0` project cannot reference a `-windows` one.
- **Machine configuration is changed only when `Wolfstare:Enforcement:ModifySystem` is `true`. Default `false`.** Without it the sinkhole and proxy still run on their ports and can be tested by pointing a client at them, but the machine's DNS, proxy, firewall, and browser policy are untouched. The installer (phase 5) sets it.
- Blocked DNS answers are `127.0.0.1` / `::1` with a 10-second TTL, so unblocking takes effect quickly despite resolver caching. Never `NXDOMAIN` (spec §7.1).
- Transparent listeners (`:80`, `:443`) only ever block. They never forward: a request arriving there was sinkholed, and forwarding it would resolve to 127.0.0.1 and loop.
- Upstream failure is `SERVFAIL`, never a fallback to an unfiltered path.
- Unit tests bind only to ephemeral loopback ports and need no admin rights.

---

### Task 1: Project, rule set cache, and wiring

**Files:**
- Create: `src/Wolfstare.Enforcement/Wolfstare.Enforcement.csproj` (`net9.0-windows`)
- Create: `src/Wolfstare.Enforcement/RuleSetCache.cs`
- Create: `tests/Wolfstare.Enforcement.Tests/` (`net9.0-windows`, xUnit)
- Modify: `src/Wolfstare.Service/Wolfstare.Service.csproj`, `tests/Wolfstare.Service.Tests/Wolfstare.Service.Tests.csproj` → `net9.0-windows`

**Produces:** `sealed class RuleSetCache { RuleSet Current { get; } void Update(RuleSet); }` — thread-safe, lock-free read, starts as `RuleSet.Empty`.

- [ ] Test: `Current` starts empty; `Update` is visible to subsequent reads; concurrent readers never observe null.
- [ ] Implement with a `volatile` field. Commit.

### Task 2: DNS message codec

**Files:** `src/Wolfstare.Enforcement/Dns/DnsMessage.cs`, test `Dns/DnsMessageTests.cs`

**Produces:**
- `readonly record struct DnsQuestion(ushort Id, string Name, ushort Type, ushort Class, bool RecursionDesired)`
- `static bool DnsMessage.TryParseQuery(ReadOnlySpan<byte>, out DnsQuestion)`
- `static byte[] DnsMessage.BuildSinkholeResponse(ReadOnlySpan<byte> query, DnsQuestion)` — A → 127.0.0.1, AAAA → ::1, any other type → NOERROR with no answers
- `static byte[] DnsMessage.BuildServFail(ReadOnlySpan<byte> query, DnsQuestion)`
- `const ushort TypeA = 1, TypeAaaa = 28`

Required test cases: parses a real query for `www.reddit.com` built byte-by-byte; rejects truncated input, a response (QR set), zero questions, a label pointer loop; A answer is 127.0.0.1 with TTL 10 and the query's ID and question echoed; AAAA answer is ::1; HTTPS (type 65) gets NOERROR with ANCOUNT 0; SERVFAIL sets RCODE 2; names are returned lowercase without trailing dot.

- [ ] Red, implement, green, commit.

### Task 3: DNS sinkhole server

**Files:** `src/Wolfstare.Enforcement/Dns/DnsSinkhole.cs`, test `Dns/DnsSinkholeTests.cs`

**Produces:** `sealed class DnsSinkhole(RuleSetCache rules, IReadOnlyList<IPEndPoint> upstreams, ILogger) : IAsyncDisposable` with `Task StartAsync(IPEndPoint bind, CancellationToken)`, `IPEndPoint LocalEndpoint`, and `event Action<string>? Blocked`.

Behaviour: UDP. Deny → sinkhole response. Permit → forward to upstreams in order with a 2 s timeout each, relay the first reply verbatim; all fail → SERVFAIL. Unparseable packet → dropped. Upstream replies whose ID does not match the query are discarded.

Test cases against a fake upstream UDP server on an ephemeral port: blocked name gets 127.0.0.1 and the fake upstream sees nothing; allowed name is forwarded and the upstream's exact bytes returned; allowlisted subdomain of a blocked domain is forwarded; dead upstream yields SERVFAIL; `Blocked` fires with the name; updating the cache changes the next answer without restart.

- [ ] Red, implement, green, commit.

### Task 4: Block proxy

**Files:** `src/Wolfstare.Enforcement/Proxy/BlockProxy.cs`, `Proxy/HttpRequestHead.cs`, `Proxy/BlockPage.cs`, tests `Proxy/HttpRequestHeadTests.cs`, `Proxy/BlockProxyTests.cs`

**Produces:**
- `sealed record HttpRequestHead(string Method, string Target, string Version, IReadOnlyList<KeyValuePair<string,string>> Headers)` with `static Task<HttpRequestHead?> ReadAsync(Stream, CancellationToken)` (max 16 KB head) and `string? Host` resolving from `CONNECT` target, absolute-form URI, or `Host` header, port stripped.
- `sealed class BlockProxy(RuleSetCache rules, ILogger, ICertificateProvider certificates)` with `Task StartExplicitAsync(IPEndPoint)`, `Task StartTransparentHttpAsync(IPEndPoint)`, `Task StartTransparentTlsAsync(IPEndPoint)`.
- `interface ICertificateProvider { bool CanInspect { get; } }` and `sealed class NullCertificateProvider` (`CanInspect == false`) — the MITM seam from spec §7.3.

Explicit listener: `CONNECT host:port` → deny: `403` + close; permit: dial target, `200 Connection Established`, pipe both ways. Absolute-form HTTP → deny: `403` with block page; permit: dial, rewrite to origin-form, strip `Proxy-Connection`, force `Connection: close`, pipe. Malformed head → `400`.
Transparent HTTP listener → always block page. Transparent TLS listener → always close immediately.

Test cases with a fake origin TCP server: `CONNECT` to allowed host tunnels bytes both directions; `CONNECT` to blocked host gets 403 and origin never sees a connection; absolute-form GET to allowed host arrives at origin in origin-form with `Connection: close`; blocked GET gets the HTML block page naming the host (HTML-encoded); transparent HTTP listener blocks even a host the rules permit; transparent TLS listener closes without writing; oversized head gets 400.

- [ ] Red, implement, green, commit.

### Task 5: Journalled system mutation

**Files:** `src/Wolfstare.Core/Enforcement/ISystemMutator.cs`, `src/Wolfstare.Enforcement/System/JournalledMutator.cs`, `System/IMutationJournal.cs`, `src/Wolfstare.Service/Storage/SqliteMutationJournal.cs`, tests.

**Produces:**
- Core: `interface ISystemSetting { string Key { get; } Task<string?> ReadAsync(CancellationToken); Task WriteAsync(string? value, CancellationToken); }` and `interface ISystemMutator { Task ApplyAsync(ISystemSetting, string? desired, CancellationToken); Task RestoreAllAsync(CancellationToken); }`
- `interface IMutationJournal { Task<string?> TryGetOriginalAsync(string key, CancellationToken); Task RecordOriginalAsync(string key, string? original, CancellationToken); Task<IReadOnlyList<(string Key, string? Original)>> GetAllAsync(CancellationToken); Task ForgetAsync(string key, CancellationToken); }` — originals only, recorded once.
- `sealed class JournalledMutator(IMutationJournal, IReadOnlyDictionary<string, ISystemSetting> known)`

Semantics: first `ApplyAsync` for a key reads and journals the original **before** writing; later applies do not overwrite the journalled original. `RestoreAllAsync` writes each original back then forgets it. A crash between journal and write is safe: restore writes back the original, which is a no-op if the write never happened. A journal entry for an unknown key is kept, not dropped, and logged.

Test cases with a fake in-memory setting: original journalled before write (a throwing write leaves the journal entry); second apply keeps the first original; restore writes originals and clears the journal; null original (setting absent) restores to absent; restore after a simulated crash restores; SQLite journal round-trips including null.

- [ ] Red, implement, green, commit.

### Task 6: Windows adapters

**Files:** `src/Wolfstare.Enforcement/System/InterfaceDnsSetting.cs`, `FirewallRuleSetting.cs`, `RegistryValueSetting.cs`, `SystemProxySetting.cs`, `BrowserDohPolicies.cs`, `docs/manual-e2e-phase3.md`

Each is an `ISystemSetting`. They run external commands (`netsh`) or registry writes and are **not** unit tested — per spec §13 a mocked test of a registry write proves only that the mock was called. Instead, pure helpers they depend on are tested: `netsh` argument construction and the parsing of `netsh interface ipv4 show dnsservers` output.

- `InterfaceDnsSetting(interfaceName, family)` — value is a comma-separated server list or `dhcp`.
- `FirewallRuleSetting(ruleName, direction, protocol, ports, excludedProgram)` — value is `present` / null.
- `RegistryValueSetting(hive, path, name, kind)` — value serialised as `kind:data`.
- `SystemProxySetting(userSid)` — `ProxyEnable` + `ProxyServer` + `ProxyOverride` under `HKU\<sid>`, then `InternetSetOption` refresh.
- `BrowserDohPolicies` — factory for the Chrome, Edge, and Firefox policy `RegistryValueSetting`s.
- Manual E2E checklist covering apply, verify with `nslookup`/`netsh`/`reg query`, restore, and crash-restore.

- [ ] Test pure helpers red/green. Implement adapters. Write checklist. Commit.

### Task 7: `WebsiteEnforcer` hosted service

**Files:** `src/Wolfstare.Service/Enforcement/WebsiteEnforcer.cs`, `EnforcementOptions.cs`, test `Enforcement/WebsiteEnforcerTests.cs`; modify `Program.cs`, `SessionManager` callers, `appsettings.json`.

Behaviour:
- Startup: if `ModifySystem`, call `RestoreAllAsync` first (crash recovery), then capture upstream DNS before takeover.
- Start sinkhole and proxy on configured endpoints (defaults `127.0.0.1:53`, `:8080`, `:80`, `:443`). A bind failure on `:53` marks health `Degraded` and logs; it does not crash the service.
- Every 2 s and immediately after any API call that starts or stops a session: refresh `RuleSetCache` from `SessionManager.GetEffectiveRuleSetAsync`.
- When `ModifySystem` and the effective set has any domain rule: apply DNS, proxy, firewall, and DoH policy settings. When it has none: `RestoreAllAsync`.
- Health reported through a `EnforcementHealth` singleton; `/api/status` reads it.

Test cases (with `ModifySystem=false`, ephemeral ports): starting a session makes the sinkhole block that domain within one refresh; stopping it unblocks; a port that cannot be bound yields `Degraded` in `/api/status` while the API keeps serving; with a fake mutator and `ModifySystem=true`, a domain session applies settings and ending it restores them.

- [ ] Red, implement, green, full suite, smoke test against the running service with `nslookup` pointed at the sinkhole port, commit, push.

## Phase 3 Definition of Done

- `dotnet test` green.
- With `ModifySystem=false`, a running service blocks `reddit.com` when queried directly (`nslookup reddit.com 127.0.0.1 -port=<port>` style check) and forwards `example.com`.
- No automated step modifies the development machine's network configuration.
- `docs/manual-e2e-phase3.md` exists for the privileged path.
