<#
.SYNOPSIS
Runs the isolated LS Overlay M9.10 quick or five-cycle reconnect validation.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\Codex\Projects\Gacha_Overlay\tools\dev\run-ls-m910-local.ps1"
#>
[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Quick', 'Reconnect')]
    [string]$Mode = 'Menu',
    [string]$GuildId,
    [string]$SessionHost1Id,
    [string]$SessionHost2Id,
    [string]$BackendUrl = 'http://127.0.0.1:5188'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$auditHelper = Join-Path $PSScriptRoot 'run-ls-m99-audit.ps1'
if (-not (Test-Path -LiteralPath $auditHelper)) {
    throw "Required hardened audit helper is missing: $auditHelper"
}

if ($Mode -eq 'Menu') {
    Write-Host ''
    Write-Host '1. Quick Remote Sales Validation'
    Write-Host '2. Five-Cycle Reconnect Validation'
    Write-Host '3. Quit'
    do {
        $choice = (Read-Host 'Select mode').Trim()
    } while ($choice -notin @('1', '2', '3'))

    if ($choice -eq '3') { return }
    $Mode = if ($choice -eq '1') { 'Quick' } else { 'Reconnect' }
}

$arguments = @{
    Mode = $Mode
    ReconnectCycles = 5
    BackendUrl = $BackendUrl
    AuditLabel = 'M9.10'
    StateLabel = 'M910'
}
if (-not [string]::IsNullOrWhiteSpace($GuildId)) {
    $arguments.GuildId = $GuildId
}
if (-not [string]::IsNullOrWhiteSpace($SessionHost1Id)) {
    $arguments.SessionHost1Id = $SessionHost1Id
}
if (-not [string]::IsNullOrWhiteSpace($SessionHost2Id)) {
    $arguments.SessionHost2Id = $SessionHost2Id
}

& $auditHelper @arguments
