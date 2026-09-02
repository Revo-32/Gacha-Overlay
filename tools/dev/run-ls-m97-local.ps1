<#
.SYNOPSIS
Starts the isolated M9.7 constrained Sales status write-back validation session.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\dev\run-ls-m97-local.ps1"
#>
[CmdletBinding()]
param(
    [string]$GuildId,
    [string]$TrackedHostIds,
    [string]$BackendUrl = 'http://127.0.0.1:5188'
)

$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'run-ls-m94-local.ps1'
if (-not (Test-Path -LiteralPath $helper)) {
    throw "Required validation helper is missing: $helper"
}

& $helper `
    -GuildId $GuildId `
    -TrackedHostIds $TrackedHostIds `
    -BackendUrl $BackendUrl `
    -ValidationMilestone 'M9.7'
