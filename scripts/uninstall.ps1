#requires -RunAsAdministrator
<#
.SYNOPSIS
  Uninstalls the Wolfstare service — unless a lock is active.

.DESCRIPTION
  Delegates to the service's own `uninstall` verb, which asks the running service whether any
  block session is locked and refuses if so (spec §9). A timed block cannot be escaped by
  uninstalling. This is not a defence against a determined administrator, who can always
  `sc delete` or boot to safe mode — an accepted limit (spec §2).
#>
$ErrorActionPreference = "Stop"
$exe = Join-Path $env:ProgramData "Wolfstare\bin\Wolfstare.Service.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Wolfstare does not appear to be installed."
    return
}

& $exe uninstall
switch ($LASTEXITCODE) {
    0 { Write-Host "Wolfstare uninstalled." }
    2 { Write-Warning "Uninstall refused: a block is still locked. Wait for it to end." }
    default { throw "Uninstall failed with exit code $LASTEXITCODE." }
}
