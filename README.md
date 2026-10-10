# Wolfstare

A local, bypass-resistant website and application blocker for Windows.

A Windows service running as `LocalSystem` owns all enforcement and all authority. The
browser UI and the CLI are thin clients over a documented local HTTP API — so anything that
can speak HTTP is a first-class client.

## What it does

- **Website blocking** via a local DNS sinkhole with wildcard subdomain support, plus a
  loopback proxy that serves a block page. DNS-over-HTTPS escape routes are sealed.
- **Application blocking** via IFEO redirection, matched on executable identity rather than
  install path — so you can write a rule for an application **before it is installed** and
  have it take effect the moment it first launches.
- **Timed locks** with no early exit. Not "confirm to cancel" — there is no code path that
  ends a timed lock before it expires, and moving the system clock does not help.
- **Password locks** for sessions you want to be able to end deliberately.

## What it does not do

It raises the cost of a bypass above the cost of the impulse. It is not DRM. An
administrator with physical access can always win — safe mode, offline registry edits,
booting from external media. See §2 of the design spec for the full threat model, which is
unusual: the adversary is the legitimate owner of the machine, later, in a weaker moment.

## Status

Feature-complete for v1, spec-first and test-driven. **309 tests passing**, plus a React UI.

| Phase | Scope | State |
|---|---|---|
| 1 | Core domain — rules, locks, clock-tamper handling, stop policy | Done |
| 2 | Persistence, session manager, HTTP API | Done |
| 3 | Website enforcement — DNS sinkhole, proxy, DoH sealing, reversible system changes | Done |
| 4 | App enforcement — IFEO redirection, ETW watcher, block stub | Done |
| 5 | Windows Service hosting, tamper resistance, session integrity, React UI | Done |

Remaining before real-world use: run the `docs/manual-e2e-phase*.md` checklists on live
hardware with `ModifySystem=true` — the privileged paths (DNS takeover, IFEO, process
termination) are covered by their design and by the checklists, but have not been exercised
end to end on a machine yet.

The service runs today and blocks both websites and applications. Websites: a local DNS
resolver sinkholes blocked domains (wildcards included) to a loopback proxy serving a block
page, while everything else forwards. Applications: an IFEO redirection catches a blocked exe
at launch — including one **not installed yet**, since the key is written ahead of time — and
an ETW watcher terminates a renamed executable by its signer. Block lists persist, timed locks
survive a process kill with downtime credited, and a locked session returns `423` to every
caller.

Machine configuration (DNS, proxy, firewall, browser DoH policy, IFEO keys) is changed **only**
when `Wolfstare:Enforcement:ModifySystem=true`; by default the network servers run but the
machine is left untouched, so they can be exercised on high ports with no admin rights. The
privileged paths are verified by the checklists in `docs/manual-e2e-phase*.md`.

See [`docs/superpowers/specs/2026-08-27-wolfstare-design.md`](docs/superpowers/specs/2026-08-27-wolfstare-design.md)
for the design specification and `docs/superpowers/plans/` for the phase plans.

## Requirements

- Windows 10/11 (developed against Windows 11 Home, build 26200)
- .NET 9 SDK
- Node.js 20+

## Install (real use)

From an elevated PowerShell:

```
scripts\install.ps1     # builds the UI, publishes, registers the service with enforcement on
scripts\uninstall.ps1   # refuses while a block is locked
```

The installed service runs at boot, restarts if killed, and turns on system enforcement
(`ModifySystem=true`). Open the UI at http://127.0.0.1:8437/.

## Development

```
dotnet run --project src/Wolfstare.Service    # API + UI on http://127.0.0.1:8437
cd web && npm run dev                          # Vite dev server, proxies /api to the service
dotnet test                                    # all tests
```

In development, enforcement runs on privileged ports (53/80/443) by default. To exercise it
without admin, override the ports and leave `ModifySystem` off (the default) so the machine's
DNS/proxy/registry are never touched:

```
dotnet run --project src/Wolfstare.Service -- \
  --Wolfstare:Enforcement:DnsPort=15353 --Wolfstare:Enforcement:ProxyPort=18080 \
  --Wolfstare:Enforcement:TransparentHttpPort=18081 --Wolfstare:Enforcement:TransparentTlsPort=14443
```

The API requires a bearer token, written to `%ProgramData%\Wolfstare\api.token` on each start:

```powershell
$t = Get-Content "$env:ProgramData\Wolfstare\api.token" -Raw
Invoke-RestMethod http://127.0.0.1:8437/api/status -Headers @{Authorization="Bearer $t"}
```

Override the data directory and port for a throwaway instance:

```
dotnet run --project src/Wolfstare.Service -- --Wolfstare:DataDirectory=C:/temp/wolf --Wolfstare:Port=8437
```
