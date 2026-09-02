<#
.SYNOPSIS
Runs short M9.12 Quick / five-cycle reconnect lifetime verification.
.DESCRIPTION
No soak and no automatic Discord mutations. Uses the existing hardened helper,
with isolated M9.12 output so earlier M9.11 evidence is preserved.
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
if ($Mode -eq 'Menu') {
    Write-Host 'M9.12 short resource-lifetime validation (not a long-run leak test)'
    Write-Host '1. Quick + media + repeated Settings open/close'
    Write-Host '2. Five-Cycle Backend Reconnect'
    Write-Host '3. Quit'
    do { $choice = (Read-Host 'Select mode').Trim() } while ($choice -notin @('1', '2', '3'))
    if ($choice -eq '3') { return }
    $Mode = if ($choice -eq '1') { 'Quick' } else { 'Reconnect' }
}
$arguments = @{
    Mode = $Mode
    ReconnectCycles = 5
    BackendUrl = $BackendUrl
    AuditLabel = 'M9.12'
    StateLabel = $(if ($Mode -eq 'Quick') { 'M912-Quick' } else { 'M912-Reconnect' })
}
if (-not [string]::IsNullOrWhiteSpace($GuildId)) { $arguments.GuildId = $GuildId }
if (-not [string]::IsNullOrWhiteSpace($SessionHost1Id)) { $arguments.SessionHost1Id = $SessionHost1Id }
if (-not [string]::IsNullOrWhiteSpace($SessionHost2Id)) { $arguments.SessionHost2Id = $SessionHost2Id }
& (Join-Path $PSScriptRoot 'run-ls-m99-audit.ps1') @arguments
