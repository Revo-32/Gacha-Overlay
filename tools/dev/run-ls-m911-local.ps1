<#
.SYNOPSIS
Runs the isolated LS Overlay M9.11 Remote-only quick or five-cycle reconnect validation.

.DESCRIPTION
Discord Desktop is not used or controlled by this helper. For the Quick check,
finish pairing if required, fully close Discord Desktop yourself, and send the
live-message check from Discord web or mobile. The helper only stops processes
that it started.
Reconnect keeps one WPF process alive and gates each restart on fresh WPF Chat,
Sales and Presence recovery for ten stable seconds, then an explicit user PASS.
FAIL, lost readiness, re-pair or timeout stops the run instead of advancing.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\Codex\Projects\Gacha_Overlay\tools\dev\run-ls-m911-local.ps1"
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
    Write-Host 'M9.11 Remote-only validation'
    Write-Host '1. Quick + Discord Desktop Fully Closed'
    Write-Host '2. Five-Cycle Backend Reconnect'
    Write-Host '3. Quit'
    do {
        $choice = (Read-Host 'Select mode').Trim()
    } while ($choice -notin @('1', '2', '3'))

    if ($choice -eq '3') { return }
    $Mode = if ($choice -eq '1') { 'Quick' } else { 'Reconnect' }
}

if ($Mode -eq 'Quick') {
    Write-Host ''
    Write-Host 'Important: after Remote pairing is ready, fully close Discord Desktop.' -ForegroundColor Yellow
    Write-Host 'Use Discord web or mobile for the new-message check. This helper will not close Discord for you.'
}

$arguments = @{
    Mode = $Mode
    ReconnectCycles = 5
    BackendUrl = $BackendUrl
    AuditLabel = 'M9.11'
    StateLabel = $(if ($Mode -eq 'Quick') { 'M911-Quick' } else { 'M911-Reconnect' })
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
