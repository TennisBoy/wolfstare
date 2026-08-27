# Wolfstare

A local, bypass-resistant website and application blocker for Windows, in the spirit of
Cold Turkey Blocker.

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

Early development. See [`docs/superpowers/specs/2026-08-27-wolfstare-design.md`](docs/superpowers/specs/2026-08-27-wolfstare-design.md)
for the full design specification.

## Requirements

- Windows 10/11 (developed against Windows 11 Home, build 26200)
- .NET 9 SDK
- Node.js 20+

## Development

```
dotnet run --project src/Wolfstare.Service -- --console   # elevated; runs the full stack
cd web && npm run dev                                      # Vite dev server, proxies /api
dotnet test                                                # all tests
```

`--console` runs the entire service as an ordinary console application, so development
needs no install/uninstall cycle.
