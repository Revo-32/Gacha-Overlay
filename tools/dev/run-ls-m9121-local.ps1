<#
.SYNOPSIS
Runs the M9.12.1 ordinary-member pairing check with an isolated Backend and WPF.
.DESCRIPTION
Operator-only helper, not end-user onboarding. Starting the Backend upserts its
Target Guild command default in Discord. No roles or Integration overrides are
modified. A human must invoke the valid/invalid pairing commands in Discord.
#>
[CmdletBinding()]
param(
    [string]$GuildId,
    [string]$SessionHost1Id,
    [string]$SessionHost2Id,
    [string]$BackendUrl = 'http://127.0.0.1:5188'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Write-Host 'M9.12.1: ordinary-member pairing permission validation'
Write-Host 'Starting this helper updates the app command default in the Target Guild.'
Write-Host 'Keep server roles and Integration overrides unchanged; use a non-admin account.'
$arguments = @{
    Mode = 'Quick'
    BackendUrl = $BackendUrl
    AuditLabel = 'M9.12.1'
    StateLabel = 'M9121-Pair'
}
if (-not [string]::IsNullOrWhiteSpace($GuildId)) { $arguments.GuildId = $GuildId }
if (-not [string]::IsNullOrWhiteSpace($SessionHost1Id)) { $arguments.SessionHost1Id = $SessionHost1Id }
if (-not [string]::IsNullOrWhiteSpace($SessionHost2Id)) { $arguments.SessionHost2Id = $SessionHost2Id }
& (Join-Path $PSScriptRoot 'run-ls-m99-audit.ps1') @arguments
