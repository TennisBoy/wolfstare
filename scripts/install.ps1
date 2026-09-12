#requires -RunAsAdministrator
<#
.SYNOPSIS
  Publishes Wolfstare and installs it as a Windows Service with enforcement enabled.

.DESCRIPTION
  Builds a self-contained publish of the service and the block stub, copies them to
  %ProgramData%\Wolfstare\bin, writes the ModifySystem=true configuration, then registers and
  starts the service. The service auto-starts at boot and restarts on failure (set by the
  service's own `install` verb), which is the tamper-resistance baseline from spec §9.

  Run from an elevated PowerShell. This DOES take over the machine's DNS/proxy/browser policy
  while a session with domain rules is active — that is the point — so install it only when you
  mean to use it.
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:ProgramData "Wolfstare\bin"

Write-Host "Building the UI..."
Push-Location (Join-Path $repoRoot "web")
try {
    npm install
    npm run build   # emits into src\Wolfstare.Service\wwwroot, which publish then copies
} finally {
    Pop-Location
}

Write-Host "Publishing Wolfstare ($Configuration)..."
dotnet publish (Join-Path $repoRoot "src\Wolfstare.Service") -c $Configuration -o $installDir
dotnet publish (Join-Path $repoRoot "src\Wolfstare.BlockStub") -c $Configuration -o $installDir

# Turn on system modification for the installed service (off by default everywhere else).
$appsettings = Join-Path $installDir "appsettings.Production.json"
@'
{ "Wolfstare": { "Enforcement": { "ModifySystem": true } } }
'@ | Set-Content -Path $appsettings -Encoding utf8

$exe = Join-Path $installDir "Wolfstare.Service.exe"
Write-Host "Registering the service..."
& $exe install
if ($LASTEXITCODE -ne 0) { throw "Service install failed with exit code $LASTEXITCODE." }

Write-Host "Wolfstare is installed and running. Open the UI at http://127.0.0.1:8437/"
