# PreToolUse hook: refuse tool calls that would get around the INSTALLED Wolfstare instance.
#
# Why this exists: Claude runs with broad permissions on a machine where Wolfstare is installed
# and enforcing real blocks. The unlock challenge is PNG-only to make scripting it awkward, but
# Claude can read images, so "unlock it for me" would otherwise be a one-line bypass. See
# "The installed instance is not a dev target" in CLAUDE.md.
#
# This is a tripwire, not a wall. It pattern-matches the tool input, so an indirect route (a
# script file, a URL assembled at runtime) can get past it. The CLAUDE.md rule is what binds.
#
# Fails closed: input that cannot be parsed is denied, in keeping with the rest of the project.

$ErrorActionPreference = 'Stop'

function Deny([string]$reason) {
    @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

try {
    $call = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $tool = [string]$call.tool_name
    $text = ($call.tool_input | ConvertTo-Json -Depth 20 -Compress)
} catch {
    Deny 'guard-live-instance: could not parse the hook input, so the call is refused (fail closed).'
}

$livePort = '\b8437\b'
$suffix   = ' The installed Wolfstare instance is not a dev target; use a throwaway instance on another port (see CLAUDE.md).'

# 1. The unlock path on the live port: fetching the challenge, submitting a retype, stopping.
if ($text -match $livePort -and $text -match '(?i)unlock|challenge|/stop\b') {
    Deny ('Refused: this touches the unlock/stop/challenge path on port 8437.' + $suffix)
}

# 2. Any browser or fetch tool pointed at the live UI, where the challenge is on screen.
if ($tool -match '(?i)playwright|browser|WebFetch' -and $text -match $livePort) {
    Deny ('Refused: opening the installed instance (port 8437) in a browser or fetch tool.' + $suffix)
}

# 3. Stopping, deleting, or reconfiguring the installed service.
if ($text -match '(?i)\bsc(\.exe)?\s+(stop|delete|config|sdset|failure)\s+"?wolfstare' -or
    $text -match '(?i)\b(Stop|Remove|Set|Suspend)-Service\b[^|;]*wolfstare' -or
    $text -match '(?i)\bnet(\.exe)?\s+stop\s+"?wolfstare') {
    Deny ('Refused: stopping, deleting, or reconfiguring the installed Wolfstare service.' +
          ' If no session is locked and this is a deliberate reinstall, run scripts\install.ps1 or scripts\uninstall.ps1, which carry their own guards.')
}

exit 0
