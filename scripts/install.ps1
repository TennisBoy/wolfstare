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

# The block stub goes to a world-runnable location, NOT the locked data directory: a standard
# user's blocked launch (via IFEO) must be able to execute it to see the block screen.
$stubDir = Join-Path $env:ProgramFiles "Wolfstare"
$stubPath = Join-Path $stubDir "Wolfstare.BlockStub.exe"

Write-Host "Publishing Wolfstare ($Configuration)..."
dotnet publish (Join-Path $repoRoot "src\Wolfstare.Service") -c $Configuration -o $installDir
dotnet publish (Join-Path $repoRoot "src\Wolfstare.BlockStub") -c $Configuration -o $stubDir

# Turn on system modification and point enforcement at the world-runnable stub.
$appsettings = Join-Path $installDir "appsettings.Production.json"
$stubJson = $stubPath -replace '\\', '\\'
@"
{ "Wolfstare": { "Enforcement": { "ModifySystem": true, "StubPath": "$stubJson" } } }
"@ | Set-Content -Path $appsettings -Encoding utf8

$exe = Join-Path $installDir "Wolfstare.Service.exe"
Write-Host "Registering the service..."
& $exe install
if ($LASTEXITCODE -ne 0) { throw "Service install failed with exit code $LASTEXITCODE." }

Write-Host "Wolfstare is installed and running. Open the UI at http://127.0.0.1:8437/"
