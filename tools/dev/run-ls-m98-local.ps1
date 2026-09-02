<#
.SYNOPSIS
Starts the isolated M9.8 GTA Session HUD and Sales notification validation session.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\dev\run-ls-m98-local.ps1"
#>
[CmdletBinding()]
param(
    [string]$GuildId,
    [string]$SessionHost1Id,
    [string]$SessionHost2Id,
    [string]$BackendUrl = 'http://127.0.0.1:5188'
)

$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'run-ls-m94-local.ps1'
if (-not (Test-Path -LiteralPath $helper)) {
    throw "Required validation helper is missing: $helper"
}

$helperArguments = @{
    GuildId = $GuildId
    BackendUrl = $BackendUrl
    ValidationMilestone = 'M9.8.1'
}
if (-not $PSBoundParameters.ContainsKey('SessionHost1Id')) {
    $SessionHost1Id = Read-Host 'GTA Session Host 1 Discord User ID [leave blank to disable Session HUD]'
}
if (-not $PSBoundParameters.ContainsKey('SessionHost2Id')) {
    $SessionHost2Id = Read-Host 'GTA Session Host 2 Discord User ID [optional]'
}
$helperArguments.SessionHost1Id = $SessionHost1Id
$helperArguments.SessionHost2Id = $SessionHost2Id

& $helper @helperArguments
