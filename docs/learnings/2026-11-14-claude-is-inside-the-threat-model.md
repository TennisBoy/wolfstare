# Claude is inside the threat model

A review of the Claude Code setup found that every guardrail in this repo was prose, and that
the agent itself was a route around the product.

## What surprised us

- **The PNG-only challenge assumes a reader that cannot see.** The design treats
  screenshot-to-OCR as the least-friction bypass. Claude reads images directly, so with the
  installed service on 8437 and `CLAUDE.md` explaining how to get the API token, "unlock it
  for me" was a single request. Nothing in the service was wrong; the assumption about the
  caller was.
- **"Never commit on `main`" was enforced by nothing.** No git hook, no branch protection, and
  the GitHub MCP tools can write to the remote without touching a local hook at all. Only the
  remote ruleset covers that path.
- **A pattern-matching hook is a tripwire, not a wall.** `guard-live-instance.ps1` refuses the
  obvious forms (unlock, stop, challenge on 8437; browser to 8437; service stop or delete). A
  URL assembled at runtime or a script file gets past it. That is acceptable only because the
  `CLAUDE.md` rule is what binds and the hook makes a violation deliberate and visible — the
  same reasoning as "an escape hatch would have to show up in a diff".
- **The newest `csharp-ls` does not install on the .NET 9 SDK.** Versions 0.21.0 and later
  fail with a missing `DotnetToolSettings.xml`; 0.20.0 is the last that installs. Same family
  of problem as `dotnet add package` defaulting to 10.x.

## What changed

The rule now lives in `CLAUDE.md` ("The installed instance is not a dev target"), backed by
the hook in `.claude/`, `.githooks/pre-commit`, and a "Protect main" ruleset on GitHub.
