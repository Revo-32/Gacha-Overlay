<# Offline regressions only: no Discord, network, credentials, or application launch. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LSOverlay.M99.Audit.psm1') -Force
$script:checks = 0
function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAILED: $Description" }
    $script:checks++
}
function Assert-Throws([scriptblock]$Action, [string]$Description) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-Check $threw $Description
}

$run = '11111111111111111111111111111111'
$epoch = (@('ABCDEF12') * 8) -join '-'
$nextEpoch = (@('ABCDEF34') * 8) -join '-'
$now = [DateTimeOffset]::UtcNow
function New-Report {
    [pscustomobject]@{
        Schema = 'LSOverlay.WpfRecovery.v1'; RunId = $run; ProcessId = 123
        ObservedAtUtc = $now.ToString('O'); Attempt = 1L; BackendEpoch = $epoch
        SalesTrackingEnabled = $true; ChatSnapshotApplied = $true; ChatStreamReady = $true
        PresenceSnapshotApplied = $true; PresenceStreamLive = $true
        SalesSnapshotComplete = $true; SalesStreamReady = $true
        AuthenticationRequired = $false; TerminalFailure = $false; AttemptEnded = $false
        Ready = $true
    }
}
function Get-State($Report) {
    Get-LsWpfRecoveryState -Report $Report -RunId $run -ExpectedProcessId 123 -Now $now
}
$report = New-Report
$ready = Get-State $report
Assert-Check $ready.Ready 'All six signals pass; empty Sales needs no fabricated row count'
Assert-Check (-not (Get-State $null).Ready) 'Missing evidence cannot pass'
foreach ($flag in @('SalesTrackingEnabled', 'ChatSnapshotApplied', 'ChatStreamReady',
        'PresenceSnapshotApplied', 'PresenceStreamLive', 'SalesSnapshotComplete', 'SalesStreamReady')) {
    $report = New-Report
    $report.$flag = $false
    Assert-Check (-not (Get-State $report).Ready) "$flag is independently required despite Ready=true"
}
foreach ($field in @('Schema', 'RunId', 'ProcessId')) {
    $report = New-Report
    $report.$field = 'wrong'
    Assert-Check (-not (Get-State $report).Ready) "$field identity mismatch rejected"
}
foreach ($seconds in @(-6, 3)) {
    $report = New-Report
    $report.ObservedAtUtc = $now.AddSeconds($seconds).ToString('O')
    Assert-Check (-not (Get-State $report).Ready) 'Stale or future heartbeat rejected'
}
foreach ($flag in @('AuthenticationRequired', 'TerminalFailure')) {
    $report = New-Report
    $report.$flag = $true
    $state = Get-State $report
    Assert-Check ($state.Fatal -and -not $state.Ready) "$flag is terminal for this audit"
}
$report = New-Report
$report.AttemptEnded = $true
Assert-Check (-not (Get-State $report).Ready) 'Ended connection cannot pass'
$report = New-Report
$report.Attempt = 0
Assert-Check (-not (Get-State $report).Ready) 'Unstarted connection cannot pass'
$report = New-Report
$report.ChatStreamReady = 'true'
Assert-Check (-not (Get-State $report).Ready) 'String truthiness rejected'
$report = New-Report
$report.PSObject.Properties.Remove('SalesStreamReady')
Assert-Check (-not (Get-State $report).Ready) 'Missing flag rejected under strict mode'
$report = New-Report
$report.BackendEpoch = 'untrusted text'
Assert-Check (-not (Get-State $report).Ready) 'Invalid epoch rejected'
$report = New-Report
$state = Get-LsWpfRecoveryState -Report $report -RunId $run -ExpectedProcessId 123 -Now $now -PreviousBackendEpoch $epoch
Assert-Check (-not $state.Ready -and $state.Reason -eq 'WaitingForNewBackend') 'Old Backend epoch cannot pass restart'
$report.BackendEpoch = $nextEpoch
$state = Get-LsWpfRecoveryState -Report $report -RunId $run -ExpectedProcessId 123 -Now $now -PreviousBackendEpoch $epoch
Assert-Check $state.Ready 'New Backend epoch is accepted'

$gate = New-LsRecoveryGate
$missing = Get-State $null
Assert-Check ((Update-LsRecoveryGate $gate $missing 0) -eq 'Waiting') 'No readiness is not success'
Assert-Check ((Update-LsRecoveryGate $gate $ready 20) -eq 'Stabilizing') 'Recovery starts stable interval'
Assert-Check ((Update-LsRecoveryGate $gate $ready 29.9) -eq 'Stabilizing') 'Less than ten seconds cannot pass'
Assert-Check ((Update-LsRecoveryGate $gate $missing 30) -eq 'Waiting') 'Readiness drop resets stability'
Assert-Check ((Update-LsRecoveryGate $gate $ready 31) -eq 'Stabilizing') 'Recovery after drop restarts interval'
Assert-Check ((Update-LsRecoveryGate $gate $ready 41) -eq 'Ready') 'Ten stable seconds pass'
$report = New-Report
$report.Attempt = 2
$newAttempt = Get-State $report
Assert-Check ((Update-LsRecoveryGate $gate $newAttempt 42) -eq 'Stabilizing') 'New attempt resets stability'
$report.BackendEpoch = $nextEpoch
$changedEpoch = Get-State $report
Assert-Check ((Update-LsRecoveryGate $gate $changedEpoch 51) -eq 'Stabilizing') 'New epoch resets stability'
Assert-Check ((Update-LsRecoveryGate $gate $changedEpoch 120) -eq 'Timeout') 'Timeout is not success'
$report.AuthenticationRequired = $true
Assert-Check ((Update-LsRecoveryGate $gate (Get-State $report) 10) -eq 'Failed') 'Re-pair is a failed recovery'

# Load only explicitly selected functions from the real helper, never its entry point.
$parseTokens = $null
$parseErrors = $null
$helperPath = Join-Path $PSScriptRoot 'run-ls-m99-audit.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($helperPath, [ref]$parseTokens, [ref]$parseErrors)
Assert-Check ($parseErrors.Count -eq 0) 'Helper parses in Windows PowerShell'
foreach ($name in @('Confirm-WpfRecovery', 'Write-RecoveryEvidence')) {
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $definition.Extent.Text
}
$activeWpfProcess = [pscustomobject]@{ Id = 123; HasExited = $false }
$backendProcess = [pscustomobject]@{ HasExited = $false }
$script:mockAnswer = 'PASS'
$script:mockState = $ready
function Read-Host { param($Prompt) return $script:mockAnswer }
function Read-WpfRecoveryState { param($PreviousBackendEpoch) return $script:mockState }
function New-Check {
    [pscustomobject]@{ Cycle = 1; Status = 'AwaitingUserConfirmation'; BackendEpoch = $epoch
        WpfAttempt = 1L; UserConfirmed = $false }
}
foreach ($answer in @('PASS', 'pass', 'PaSs', ' pass ')) {
    $script:mockAnswer = $answer
    $check = New-Check
    Confirm-WpfRecovery $check
    Assert-Check ($check.UserConfirmed -and $check.Status -eq 'RecoveredAndUserConfirmed') 'Explicit PASS accepts case variants and surrounding whitespace'
}
foreach ($answer in @('', '  ', 'FAIL', 'fail', 'PASS FAIL', 'yes')) {
    $script:mockAnswer = $answer
    $check = New-Check
    Assert-Throws { Confirm-WpfRecovery $check } 'Enter or anything except explicit PASS must stop'
    Assert-Check (-not $check.UserConfirmed) 'Rejected confirmation never records success'
}
$script:mockAnswer = 'PASS'
foreach ($lost in @($missing, $newAttempt, $changedEpoch)) {
    $script:mockState = $lost
    $check = New-Check
    Assert-Throws { Confirm-WpfRecovery $check } 'Changed state while waiting for confirmation must stop'
}
$script:mockState = $ready
$activeWpfProcess.HasExited = $true
Assert-Throws { Confirm-WpfRecovery (New-Check) } 'Closed WPF cannot pass'
$activeWpfProcess.HasExited = $false

# Execute the actual restart loop with offline mocks. No process or network action runs.
$loop = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.ForStatementAst] -and
        $node.Extent.Text.StartsWith('for ($cycle = 1;')
}, $true)
Assert-Check ($null -ne $loop) 'Restart loop found'
$ReconnectCycles = 5
$recoveryChecks = [System.Collections.Generic.List[object]]::new()
$previousSamples = @{}
$script:starts = 0
$script:confirmations = 0
$script:failCycle = 2
function Write-RecoveryEvidence { }
function Stop-OwnedBackend { param($Process) }
function Start-AuditBackend { param($Cycle) $script:starts++; return [pscustomobject]@{ HasExited = $false } }
function Wait-BackendReady { param($Process) }
function Wait-WpfRecovery { param($Cycle, $PreviousBackendEpoch)
    if ($Cycle -eq $script:failCycle) { throw 'Simulated recovery timeout' }
    return [pscustomobject]@{ Cycle = $Cycle; BackendEpoch = "epoch-$Cycle" }
}
function Confirm-WpfRecovery { param($Check) $script:confirmations++ }
$check = [pscustomobject]@{ BackendEpoch = 'baseline' }
Assert-Throws { Invoke-Expression $loop.Extent.Text } 'Failed cycle stops actual loop'
Assert-Check ($script:starts -eq 2 -and $script:confirmations -eq 1) 'Cycle 3 never starts after cycle 2 fails'
$script:failCycle = -1
$script:starts = 0
$script:confirmations = 0
Invoke-Expression $loop.Extent.Text
Assert-Check ($script:starts -eq 5 -and $script:confirmations -eq 5) 'All five cycles require confirmation'

# Preserve failure evidence even when no metrics were collected, without leaking IDs.
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('LSOverlay-M911-Offline-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testDirectory) | Out-Null
try {
    $summary = [ordered]@{ Status = 'Failed'; RunId = ([Guid]$run).ToString('D'); BackendEpoch = $epoch }
    $written = Write-LsAuditOutputs -OutputDirectory $testDirectory -Summary $summary -Samples @()
    Assert-Check (Test-Path -LiteralPath $written.SummaryPath) 'Failure summary saved with zero samples'
    Assert-Check (Test-Path -LiteralPath $written.MetricsPath) 'Empty metrics CSV still exists'
    Assert-Check ((Get-LsProcessAuditSummary -Samples @() -Kind WPF).Status -eq 'NotRun') 'No metrics is NotRun, not PASS'
    Assert-Check (Test-LsAuditPayload ($summary | ConvertTo-Json)) 'Segmented synthetic identifiers survive sanitization'
    Assert-Throws { Test-LsAuditPayload '{"messageContent":"private"}' } 'Content cannot enter summary'
    Assert-Throws { Test-LsAuditPayload '{"id":"123456789012345678"}' } 'Discord-like IDs cannot enter summary'
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-RecoveryEvidence'
    }, $true)
    Invoke-Expression $definition.Extent.Text
    $Mode = 'Reconnect'; $stateCreated = $true; $lastAuditDirectory = $testDirectory
    $auditRunId = $run; $recoveryOutcome = 'Failed'; $currentRecoveryCycle = 2
    $lastRecoveryState = $missing
    Write-RecoveryEvidence
    $saved = Get-Content -LiteralPath (Join-Path $testDirectory 'm99-reconnect-recovery.json') -Raw | ConvertFrom-Json
    Assert-Check ($saved.Status -eq 'Failed' -and $saved.CurrentCycle -eq 2) 'Failed cycle survives cleanup in sanitized evidence'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    $parent = [IO.Directory]::GetParent($resolved).FullName.TrimEnd('\')
    if ($parent -cne [IO.Path]::GetTempPath().TrimEnd('\') -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('LSOverlay-M911-Offline-')) {
        throw 'Unsafe offline test cleanup path'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "M9.11 offline recovery checks passed ($script:checks assertions)."
