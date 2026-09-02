<#
.SYNOPSIS
Runs the isolated LS Overlay readiness, soak, and recovery audit.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\Codex\Projects\Gacha_Overlay\tools\dev\run-ls-m99-audit.ps1"
#>
[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Quick', 'Soak', 'Reconnect')]
    [string]$Mode = 'Menu',
    [ValidateRange(1, 1440)]
    [int]$SoakMinutes = 30,
    [ValidateRange(2, 60)]
    [int]$SampleIntervalSeconds = 5,
    [ValidateRange(1, 20)]
    [int]$ReconnectCycles = 5,
    [string]$GuildId,
    [string]$SessionHost1Id,
    [string]$SessionHost2Id,
    [string]$BackendUrl = 'http://127.0.0.1:5188',
    [string]$AuditLabel = 'M9.9',
    [string]$StateLabel = 'M99'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$backendProject = Join-Path $repositoryRoot 'src\LSOverlay.Backend\LSOverlay.Backend.csproj'
$wpfProject = Join-Path $repositoryRoot 'src\GachaOverlay.App\GachaOverlay.App.csproj'
$solutionPath = Join-Path $repositoryRoot 'GachaOverlay.sln'
$publishProfile = Join-Path $repositoryRoot 'src\GachaOverlay.App\Properties\PublishProfiles\win-x64-singlefile.pubxml'
$auditModule = Join-Path $PSScriptRoot 'LSOverlay.M99.Audit.psm1'
$statePrefix = "LSOverlay-$StateLabel-Audit-"
$auditRunId = [Guid]::NewGuid().ToString('N')
$stateDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
    $statePrefix + $auditRunId)
$backendOutput = Join-Path $stateDirectory 'backend-release'
$wpfOutput = Join-Path $stateDirectory 'wpf-release'
$summaryOutput = Join-Path $stateDirectory 'sanitized-summary'
$lastAuditDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "LSOverlay-$StateLabel-LastAudit"
$shutdownFile = Join-Path $stateDirectory 'shutdown.request'
$wpfRecoveryFile = Join-Path $stateDirectory 'wpf-recovery.json'
$wpfRecoveryDirectoryName = 'LSO_DEV_RECOVERY_AUDIT_DIRECTORY'
$wpfRecoveryRunName = 'LSO_DEV_RECOVERY_AUDIT_RUN_ID'
$recoveryChecks = [System.Collections.Generic.List[object]]::new()
$recoveryOutcome = 'NotStarted'
$currentRecoveryCycle = 0
$lastRecoveryState = $null
$tokenName = 'LSO_DISCORD_BOT_TOKEN'
$guildName = 'LSO_DISCORD_GUILD_ID'
$legacyHostsName = 'LSO_TRACKED_HOST_IDS'
$host1Name = 'LSO_SESSION_HOST_1_ID'
$host2Name = 'LSO_SESSION_HOST_2_ID'
$stateName = 'LSO_STATE_DIRECTORY'
$listenName = 'LSO_LISTEN_URL'
$shutdownName = 'LSO_DEV_SHUTDOWN_FILE'
$backendEnvironmentNames = @(
    $tokenName,
    $guildName,
    $legacyHostsName,
    $host1Name,
    $host2Name,
    $stateName,
    $listenName,
    $shutdownName)
$secureToken = $null
$backendProcess = $null
$activeWpfProcess = $null
$stateCreated = $false
$backendLogs = [System.Collections.Generic.List[object]]::new()
$auditStarted = $null
$auditFinished = $null

Import-Module -Name $auditModule -Force
$samples = New-LsAuditAccumulator
$previousSamples = @{}

function Assert-RequiredPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required path is missing: $Path"
    }
}

function Normalize-DiscordId {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [switch]$Optional)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        if ($Optional) { return '' }
        throw "$Name is required."
    }

    [UInt64]$parsed = 0
    if (-not [UInt64]::TryParse($Value.Trim(), [ref]$parsed) -or $parsed -eq 0) {
        throw "$Name must be a valid Discord ID."
    }

    return $parsed.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Assert-LoopbackBackendUrl {
    param([Parameter(Mandatory = $true)][string]$Value)

    $uri = [Uri]$Value
    if ($uri.Scheme -ne 'http' -or
        -not ($uri.IsLoopback -or $uri.Host -in @('localhost', '127.0.0.1', '::1')) -or
        $uri.AbsolutePath -ne '/') {
        throw 'BackendUrl must be an HTTP loopback origin without a path.'
    }

    return $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
}

function Assert-BackendEndpointAvailable {
    param([Parameter(Mandatory = $true)][string]$Url)

    $uri = [Uri]$Url
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($uri.Host, $uri.Port)
        if ($connect.Wait(750) -and $client.Connected) {
            throw "BackendUrl $Url is already in use. No unknown process was stopped."
        }
    }
    catch [System.AggregateException] {
    }
    catch [System.Net.Sockets.SocketException] {
    }
    finally {
        $client.Dispose()
    }
}

function Invoke-CheckedDotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Clear-BackendEnvironment {
    foreach ($name in $backendEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}

function Start-AuditBackend {
    param([Parameter(Mandatory = $true)][int]$Cycle)

    if (Test-Path -LiteralPath $shutdownFile) {
        [System.IO.File]::Delete($shutdownFile)
    }

    $stdout = Join-Path $stateDirectory ("backend.$Cycle.stdout.log")
    $stderr = Join-Path $stateDirectory ("backend.$Cycle.stderr.log")
    $pointer = [IntPtr]::Zero
    $plain = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        [Environment]::SetEnvironmentVariable($tokenName, $plain, 'Process')
        [Environment]::SetEnvironmentVariable($guildName, $GuildId, 'Process')
        [Environment]::SetEnvironmentVariable($legacyHostsName, $null, 'Process')
        [Environment]::SetEnvironmentVariable($host1Name, $SessionHost1Id, 'Process')
        [Environment]::SetEnvironmentVariable($host2Name, $SessionHost2Id, 'Process')
        [Environment]::SetEnvironmentVariable($stateName, $stateDirectory, 'Process')
        [Environment]::SetEnvironmentVariable($listenName, $BackendUrl, 'Process')
        [Environment]::SetEnvironmentVariable($shutdownName, $shutdownFile, 'Process')
        $dotnetHost = (Get-Command dotnet -ErrorAction Stop).Source
        $backendDll = Join-Path $backendOutput 'LSOverlay.Backend.dll'
        $process = Start-Process -FilePath $dotnetHost `
            -ArgumentList @('"' + $backendDll + '"') `
            -WorkingDirectory $backendOutput `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -PassThru
        $backendLogs.Add([pscustomobject]@{
            Cycle = $Cycle
            StandardOutput = $stdout
            StandardError = $stderr
        })
        return $process
    }
    finally {
        Clear-BackendEnvironment
        $plain = $null
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

function Wait-BackendReady {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $maximumAttempts = 480
    $healthWasReady = $false
    $backendExited = $false
    $latestEntry = $backendLogs[$backendLogs.Count - 1]
    $latestLog = $latestEntry.StandardOutput
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $nextProgressSeconds = 5
    for ($attempt = 0; $attempt -lt $maximumAttempts -and $timer.Elapsed.TotalSeconds -lt 120; $attempt++) {
        if ($Process.HasExited) {
            $backendExited = $true
            break
        }

        $logText = ''
        if (Test-Path -LiteralPath $latestLog) {
            $logText = Read-BackendLogText -Path $latestLog
        }
        try {
            $health = Invoke-RestMethod -Uri ($BackendUrl + '/healthz') -TimeoutSec 1
            $healthWasReady = $health.status -eq 'ok'
            if ($healthWasReady -and
                $logText.IndexOf(
                    'Discord pairing command: Available',
                    [StringComparison]::Ordinal) -ge 0) {
                return
            }
        }
        catch {
        }

        if ($healthWasReady -and $timer.Elapsed.TotalSeconds -ge $nextProgressSeconds) {
            $elapsedSeconds = [int]$timer.Elapsed.TotalSeconds
            $disconnectCount = ([regex]::Matches(
                    $logText,
                    'Discord Gateway disconnected',
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
            if ($disconnectCount -gt 0) {
                Write-Host (
                    "Discord Gateway is still reconnecting ({0}/120 seconds; disconnects observed: {1})." -f
                    $elapsedSeconds,
                    $disconnectCount) -ForegroundColor Yellow
            }
            else {
                Write-Host (
                    "Backend is ready; waiting for Discord Gateway and pairing command ({0}/120 seconds)." -f
                    $elapsedSeconds) -ForegroundColor Yellow
            }
            $nextProgressSeconds = if ($nextProgressSeconds -eq 5) { 30 } else { $nextProgressSeconds + 30 }
        }
        Start-Sleep -Milliseconds 250
    }

    $logText = if (Test-Path -LiteralPath $latestLog) {
        Read-BackendLogText -Path $latestLog
    }
    else {
        ''
    }
    $failureLogHint = Join-Path $lastAuditDirectory ("failure.{0}.standardoutput.log" -f $latestEntry.Cycle)
    if ($logText.IndexOf(
            'Discord rejected required privileged intents.',
            [StringComparison]::Ordinal) -ge 0) {
        throw "Discord rejected required Gateway intents. Enable Presence Intent and Message Content Intent, then retry. Sanitized log: $failureLogHint"
    }
    if ($logText.IndexOf(
            'Discord authentication failed',
            [StringComparison]::Ordinal) -ge 0) {
        throw "Discord Bot authentication failed. Verify the Bot token, then retry. Sanitized log: $failureLogHint"
    }
    if ($backendExited) {
        throw "Backend exited before readiness. Sanitized log: $failureLogHint"
    }
    if ($logText.IndexOf(
            'Discord Gateway disconnected category=WebSocketException',
            [StringComparison]::Ordinal) -ge 0) {
        throw "Backend started, but Discord Gateway WebSocket connection remained unavailable after 120 seconds. Check the network, VPN/proxy/firewall, or Discord service status, then retry. Sanitized log: $failureLogHint"
    }
    if ($logText.IndexOf(
            'Discord pairing command registration unavailable',
            [StringComparison]::Ordinal) -ge 0) {
        throw "Discord connected, but the pairing command could not be registered. Verify the Bot application/Guild configuration, then retry. Sanitized log: $failureLogHint"
    }
    if ($healthWasReady) {
        throw "Backend is healthy, but Discord and its pairing command did not become ready within 120 seconds. Sanitized log: $failureLogHint"
    }

    throw "Backend health endpoint did not become ready within 120 seconds. Sanitized log: $failureLogHint"
}

function Start-AuditWpf {
    foreach ($name in $backendEnvironmentNames) {
        if (-not [string]::IsNullOrEmpty(
                [Environment]::GetEnvironmentVariable($name, 'Process'))) {
            throw "Backend environment isolation failed for $name."
        }
    }

    try {
        [Environment]::SetEnvironmentVariable($wpfRecoveryDirectoryName, $stateDirectory, 'Process')
        [Environment]::SetEnvironmentVariable($wpfRecoveryRunName, $auditRunId, 'Process')
        return Start-Process -FilePath (Join-Path $wpfOutput 'GachaOverlay.App.exe') `
            -WorkingDirectory $wpfOutput `
            -PassThru
    }
    finally {
        [Environment]::SetEnvironmentVariable($wpfRecoveryDirectoryName, $null, 'Process')
        [Environment]::SetEnvironmentVariable($wpfRecoveryRunName, $null, 'Process')
    }
}

function Read-WpfRecoveryState {
    param([string]$PreviousBackendEpoch = '')

    $report = $null
    if (Test-Path -LiteralPath $wpfRecoveryFile) {
        try { $report = Read-BackendLogText -Path $wpfRecoveryFile | ConvertFrom-Json }
        catch { }
    }
    return Get-LsWpfRecoveryState -Report $report -RunId $auditRunId `
        -ExpectedProcessId $activeWpfProcess.Id -PreviousBackendEpoch $PreviousBackendEpoch
}

function Wait-WpfRecovery {
    param([int]$Cycle, [string]$PreviousBackendEpoch = '')

    $gate = New-LsRecoveryGate
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $nextSample = 0
    $nextProgress = 0
    while ($true) {
        if ($activeWpfProcess.HasExited -or $backendProcess.HasExited) {
            throw "Recovery cycle $Cycle failed: a helper-owned process exited."
        }
        $state = Read-WpfRecoveryState -PreviousBackendEpoch $PreviousBackendEpoch
        $script:lastRecoveryState = $state
        $elapsed = $timer.Elapsed.TotalSeconds
        $decision = Update-LsRecoveryGate -Gate $gate -State $state -ElapsedSeconds $elapsed
        if ($elapsed -ge $nextSample) {
            Add-CurrentSamples
            $nextSample = $elapsed + $SampleIntervalSeconds
        }
        if ($decision -in @('Failed', 'Timeout')) {
            throw "Recovery cycle $Cycle $decision; WPF state=$($state.Reason). No next restart will run."
        }
        if ($decision -eq 'Ready') {
            Write-Host "Cycle ${Cycle}: Chat, Sales and Presence recovered and stayed ready for 10 seconds." -ForegroundColor Green
            return [pscustomobject]@{
                Cycle = $Cycle
                Status = 'AwaitingUserConfirmation'
                RecoverySeconds = [Math]::Round($elapsed, 3)
                StableSeconds = 10
                WpfAttempt = $state.Attempt
                BackendEpoch = $state.BackendEpoch
                UserConfirmed = $false
            }
        }
        if ($elapsed -ge $nextProgress) {
            Write-Host ("Cycle {0}: {1}, WPF={2} ({3}/120 seconds)." -f
                $Cycle, $decision, $state.Reason, [int]$elapsed) -ForegroundColor Yellow
            $nextProgress = $elapsed + 10
        }
        Start-Sleep -Milliseconds 500
    }
}

function Confirm-WpfRecovery {
    param([Parameter(Mandatory = $true)]$Check)

    Write-Host 'Confirm on screen: Chat updates, Sales is healthy (empty is allowed), selected Session is shown.'
    Write-Host 'Also check: WPF stayed open; no re-pair, duplicate rows or sound; own-sale controls recover when applicable.'
    $answer = (Read-Host "Cycle $($Check.Cycle): type PASS if these checks passed, otherwise FAIL (case-insensitive)").Trim()
    if ($answer -ine 'PASS') {
        $Check.Status = 'UserRejected'
        throw "Recovery cycle $($Check.Cycle) was not confirmed. No next restart will run."
    }
    $state = Read-WpfRecoveryState
    $script:lastRecoveryState = $state
    if (-not $state.Ready -or $state.BackendEpoch -ne $Check.BackendEpoch -or
        $state.Attempt -ne $Check.WpfAttempt -or $activeWpfProcess.HasExited -or $backendProcess.HasExited) {
        $Check.Status = 'RecoveryLostDuringConfirmation'
        throw "Recovery cycle $($Check.Cycle) lost its ready state during confirmation. No next restart will run."
    }
    $Check.Status = 'RecoveredAndUserConfirmed'
    $Check.UserConfirmed = $true
}

function Write-RecoveryEvidence {
    if ($Mode -ne 'Reconnect' -or -not $stateCreated) { return }
    [System.IO.Directory]::CreateDirectory($lastAuditDirectory) | Out-Null
    $evidence = [ordered]@{
        Schema = 'LSOverlay.ReconnectRecovery.v1'
        RunId = ([Guid]$auditRunId).ToString('D')
        Status = $recoveryOutcome
        CurrentCycle = $currentRecoveryCycle
        RequestedCycles = $ReconnectCycles
        Checks = @($recoveryChecks.ToArray())
        LastState = $lastRecoveryState
        WriteBackVerification = 'User checkpoint only; no automated Discord writes'
    }
    $json = $evidence | ConvertTo-Json -Depth 8
    Test-LsAuditPayload -Json $json | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $lastAuditDirectory 'm99-reconnect-recovery.json'),
        $json, [System.Text.UTF8Encoding]::new($false))
}

function Stop-OwnedBackend {
    param($Process)

    if ($null -eq $Process -or $Process.HasExited) { return }
    [System.IO.File]::WriteAllText($shutdownFile, 'stop')
    if (-not $Process.WaitForExit(10000)) {
        Stop-Process -Id $Process.Id -Force
        $Process.WaitForExit()
    }
    Assert-BackendEndpointReleased
}

function Assert-BackendEndpointReleased {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Assert-BackendEndpointAvailable -Url $BackendUrl
            return
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    throw 'Helper-owned Backend exited but its loopback endpoint is still occupied.'
}

function Stop-OwnedWpf {
    param($Process)

    if ($null -eq $Process -or $Process.HasExited) { return }
    $Process.CloseMainWindow() | Out-Null
    if (-not $Process.WaitForExit(5000)) {
        Stop-Process -Id $Process.Id -Force
        $Process.WaitForExit()
    }
}

function Add-CurrentSamples {
    foreach ($entry in @(
            [pscustomobject]@{ Kind = 'WPF'; Process = $activeWpfProcess },
            [pscustomobject]@{ Kind = 'Backend'; Process = $backendProcess })) {
        if ($null -eq $entry.Process -or $entry.Process.HasExited) {
            throw "$($entry.Kind) exited during audit sampling."
        }
        $previous = if ($previousSamples.ContainsKey($entry.Kind)) {
            $previousSamples[$entry.Kind]
        }
        else {
            $null
        }
        $sample = Get-LsProcessAuditSample `
            -Process $entry.Process `
            -Kind $entry.Kind `
            -Previous $previous
        $previousSamples[$entry.Kind] = $sample
        Add-LsAuditSample -Accumulator $samples -Sample $sample
    }
}

function Invoke-SamplingWindow {
    param([Parameter(Mandatory = $true)][int]$DurationSeconds)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        Add-CurrentSamples
        $remaining = [Math]::Max(0, ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds)
        if ($remaining -gt 0) {
            $sleepMilliseconds = [int][Math]::Ceiling(
                [Math]::Min($SampleIntervalSeconds, $remaining) * 1000)
            Start-Sleep -Milliseconds $sleepMilliseconds
        }
    }
    Add-CurrentSamples
}

function Write-ModeChecklist {
    Write-Host ''
    Write-Host "$AuditLabel USER CHECKPOINT" -ForegroundColor Green
    if ($AuditLabel -eq 'M9.12.1') {
        Write-Host '[ ] Use an ordinary account with no Administrator, Manage Guild or moderator privileges'
        Write-Host '[ ] /lsoverlay pair is visible in the configured Target Guild'
        Write-Host '[ ] A valid fresh WPF pairing code is approved with a private response'
        Write-Host '[ ] WPF receives the credential and connects as that ordinary account'
        Write-Host '[ ] An invalid code is privately rejected without changing the valid pairing'
        Write-Host '[ ] No server roles or Integration overrides were changed for this test'
        Write-Host 'Completion records capture only; explicitly report whether every check passed.'
        Write-Host ''
        return
    }
    Write-Host '[ ] Pair if this isolated Backend requests pairing'
    Write-Host '[ ] Remote Main Chat becomes Live and recent messages appear'
    Write-Host '[ ] Remote Sales becomes RemotePrimary / Complete'
    Write-Host '[ ] Session HUD shows only the selected Host slot'
    Write-Host '[ ] No duplicate Chat, Sales entry, Personal Alert, or sound'
    if ($AuditLabel -eq 'M9.10') {
        Write-Host '[ ] Navigate Discord to an unrelated channel, then minimize Discord'
        Write-Host '[ ] New Sales and reaction updates continue without opening the Sales channel'
        Write-Host '[ ] Own-message write-back works only after RemotePrimary returns'
        Write-Host '[ ] No retired Sales sensor or channel-open warning appears anywhere'
    }
    elseif ($AuditLabel -in @('M9.11', 'M9.12')) {
        Write-Host '[ ] Main Chat recent20, a new message, and a channel switch work'
        Write-Host '[ ] Own-message Sales write-back and one-shot notification work'
        Write-Host '[ ] Settings and onboarding contain no source selector or legacy sign-in UI'
        Write-Host '[ ] Fully close Discord Desktop after pairing, then use web/mobile for a test message'
        Write-Host '[ ] Chat, Sales, Session HUD, write-back and sound remain healthy with Desktop closed'
        Write-Host '[ ] No local connection status, warning, setup, or fallback appears'
    }
    if ($AuditLabel -eq 'M9.12') {
        Write-Host '[ ] Reopen Settings/onboarding and expand/collapse Queue Detail several times'
        Write-Host '[ ] Normal/forward image, sticker and custom emoji render correctly'
        Write-Host '[ ] Clear media cache and switch chat channels without stale rows or fatal errors'
        Write-Host '[ ] This short capture is not a memory-leak or multi-hour stability PASS'
    }
    if ($Mode -eq 'Reconnect') {
        Write-Host '[ ] WPF stays open through every Backend restart'
        Write-Host '[ ] No re-pair is requested and write-back returns after RemotePrimary'
        Write-Host '[ ] Each cycle waits for WPF recovery + 10 stable seconds, then requires an explicit PASS'
        Write-Host '[ ] Timeout, re-pair, missing readiness or FAIL stops the test; completion alone is not PASS'
    }
    elseif ($Mode -eq 'Soak') {
        Write-Host '[ ] Use the PC normally with Remote Chat/Sales and Session HUD active'
    }
    Write-Host ''
}

function Get-BackendEventCounts {
    $text = ($backendLogs | ForEach-Object {
        if (Test-Path -LiteralPath $_.StandardOutput) {
            Read-BackendLogText -Path $_.StandardOutput
        }
    }) -join [Environment]::NewLine
    return [ordered]@{
        GatewayDisconnects = ([regex]::Matches(
            $text,
            'Discord Gateway disconnected',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
        SlowClientDisconnects = ([regex]::Matches($text, 'SlowClient')).Count
        BackendRestartCycles = [Math]::Max(0, $backendLogs.Count - 1)
    }
}

function Read-BackendLogText {
    param([Parameter(Mandatory = $true)][string]$Path)

    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            $stream = [System.IO.FileStream]::new(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite)
            try {
                $reader = [System.IO.StreamReader]::new(
                    $stream,
                    [System.Text.Encoding]::UTF8,
                    $true,
                    4096,
                    $false)
                try {
                    return $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
        }
        catch [System.IO.IOException] {
            if ($attempt -eq 4) {
                Write-Warning 'A Backend audit log remained unavailable; its optional counters were omitted.'
                return ''
            }
            Start-Sleep -Milliseconds 100
        }
    }

    return ''
}

function Complete-SanitizedAudit {
    param([string]$Status = 'CaptureCompleted')

    $auditFinished = [DateTimeOffset]::UtcNow
    if ($null -eq $auditStarted) { $auditStarted = $auditFinished }
    $sampleArray = @($samples.Samples)
    $summary = [ordered]@{
        Schema = 'LSOverlay.M99.AuditSummary.v1'
        Mode = $Mode
        Status = $Status
        RunId = ([Guid]$auditRunId).ToString('D')
        RecoveryStatus = $recoveryOutcome
        StartedAtUtc = $auditStarted.ToString('O')
        FinishedAtUtc = $auditFinished.ToString('O')
        DurationSeconds = [Math]::Round(($auditFinished - $auditStarted).TotalSeconds, 3)
        SampleIntervalSeconds = $SampleIntervalSeconds
        Wpf = Get-LsProcessAuditSummary -Samples $sampleArray -Kind WPF
        Backend = Get-LsProcessAuditSummary -Samples $sampleArray -Kind Backend
        Remote = Get-BackendEventCounts
        Bounds = [ordered]@{
            MainChat = 20
            AuthoritativeSales = 30
            SalesJournal = 256
            PublicationJournal = 2048
            WebSocketOutbound = 256
            SessionHosts = 2
        }
        WpfSingleFileBytes = if (Test-Path -LiteralPath (Join-Path $wpfOutput 'GachaOverlay.App.exe')) {
            (Get-Item -LiteralPath (Join-Path $wpfOutput 'GachaOverlay.App.exe')).Length
        } else { $null }
        Limitations = @(
            'Managed heap is unavailable from low-overhead external process sampling.',
            'Protocol event counts are intentionally limited to sanitized Backend counters.',
            'User-visible functional checkpoints require user confirmation.'
        )
    }
    $written = Write-LsAuditOutputs `
        -OutputDirectory $summaryOutput `
        -Summary $summary `
        -Samples $sampleArray
    New-Item -ItemType Directory -Path $lastAuditDirectory -Force | Out-Null
    $modeName = $Mode.ToLowerInvariant()
    Copy-Item -LiteralPath $written.SummaryPath -Destination (
        Join-Path $lastAuditDirectory "m99-$modeName-summary.json") -Force
    Copy-Item -LiteralPath $written.MetricsPath -Destination (
        Join-Path $lastAuditDirectory "m99-$modeName-metrics.csv") -Force
    Write-Host "Sanitized $AuditLabel audit summary: $lastAuditDirectory" -ForegroundColor Cyan
}

function Preserve-SanitizedFailureLogs {
    if ($backendLogs.Count -eq 0) { return }
    [System.IO.Directory]::CreateDirectory($lastAuditDirectory) | Out-Null
    foreach ($entry in $backendLogs) {
        foreach ($property in @('StandardOutput', 'StandardError')) {
            $source = $entry.$property
            if (-not (Test-Path -LiteralPath $source)) { continue }
            $content = Read-BackendLogText -Path $source
            $content = [regex]::Replace($content, '(?<!\d)\d{17,20}(?!\d)', '[ID]')
            $content = [regex]::Replace(
                $content,
                '(?i)(authorization\s*[:=]\s*|bearer\s+)[^\s,;]+',
                '$1[REDACTED]')
            $content = [regex]::Replace(
                $content,
                '(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{23,28}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{25,40}(?![A-Za-z0-9_-])',
                '[REDACTED]')
            $content = [regex]::Replace(
                $content,
                '(?i)((?:bot|access|refresh)?[_ -]?(?:token|secret|password)\s*[:=]\s*)[^\s,;]+',
                '$1[REDACTED]')
            $leaf = "failure.$($entry.Cycle).$($property.ToLowerInvariant()).log"
            [System.IO.File]::WriteAllText(
                (Join-Path $lastAuditDirectory $leaf),
                $content,
                [System.Text.UTF8Encoding]::new($false))
        }
    }
}

function Remove-IsolatedState {
    if (-not (Test-Path -LiteralPath $stateDirectory)) { return }
    $resolved = [System.IO.Path]::GetFullPath($stateDirectory)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($resolved).StartsWith(
            $statePrefix,
            [StringComparison]::Ordinal)) {
        throw "Unsafe $AuditLabel state cleanup path rejected."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

try {
    Assert-RequiredPath $solutionPath
    Assert-RequiredPath $backendProject
    Assert-RequiredPath $wpfProject
    Assert-RequiredPath $publishProfile
    Assert-RequiredPath $auditModule

    if ($Mode -eq 'Menu') {
        Write-Host '1. Quick Readiness Check'
        Write-Host '2. 30-Minute Soak'
        Write-Host '3. Reconnect Stress'
        Write-Host '4. Quit'
        do { $choice = (Read-Host 'Select mode').Trim() } while ($choice -notin @('1','2','3','4'))
        if ($choice -eq '4') { return }
        $Mode = @{ '1'='Quick'; '2'='Soak'; '3'='Reconnect' }[$choice]
    }

    [Environment]::SetEnvironmentVariable($wpfRecoveryDirectoryName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($wpfRecoveryRunName, $null, 'Process')

    $BackendUrl = Assert-LoopbackBackendUrl $BackendUrl
    Assert-BackendEndpointAvailable $BackendUrl
    if ([string]::IsNullOrWhiteSpace($GuildId)) {
        $GuildId = Read-Host 'Target Discord Guild ID'
    }
    if (-not $PSBoundParameters.ContainsKey('SessionHost1Id')) {
        $SessionHost1Id = Read-Host 'Session Host 1 Discord User ID'
    }
    if (-not $PSBoundParameters.ContainsKey('SessionHost2Id')) {
        $SessionHost2Id = Read-Host 'Session Host 2 Discord User ID [optional]'
    }
    $GuildId = Normalize-DiscordId $GuildId 'Target Discord Guild ID'
    $SessionHost1Id = Normalize-DiscordId $SessionHost1Id 'Session Host 1 Discord User ID'
    $SessionHost2Id = Normalize-DiscordId `
        $SessionHost2Id `
        'Session Host 2 Discord User ID' `
        -Optional
    if (-not [string]::IsNullOrWhiteSpace($SessionHost2Id) -and
        $SessionHost1Id -eq $SessionHost2Id) {
        throw 'Session Host 1 and Session Host 2 must use different Discord IDs.'
    }

    if (@(Get-Process -Name 'GachaOverlay.App' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Close the currently running GachaOverlay application first.'
    }
    $secureToken = Read-Host 'Discord Bot Token' -AsSecureString
    if ($secureToken.Length -eq 0) { throw 'Discord Bot Token cannot be empty.' }

    New-Item -ItemType Directory -Path $stateDirectory | Out-Null
    $stateCreated = $true
    if ($Mode -eq 'Reconnect') {
        $recoveryOutcome = 'Running'
        Write-RecoveryEvidence
    }
    Write-Host 'Preparing isolated Release audit binaries...'
    Invoke-CheckedDotNet @('restore', $solutionPath)
    Invoke-CheckedDotNet @('restore', $wpfProject, ('-p:PublishProfile=' + $publishProfile))
    Invoke-CheckedDotNet @('publish', $backendProject, '-c', 'Release', '--no-restore', '-o', $backendOutput)
    Invoke-CheckedDotNet @(
        'publish', $wpfProject, '-c', 'Release', '--no-restore',
        ('-p:PublishProfile=' + $publishProfile), '-o', $wpfOutput)

    $backendProcess = Start-AuditBackend -Cycle 0
    Wait-BackendReady $backendProcess
    $activeWpfProcess = Start-AuditWpf
    Write-ModeChecklist
    Read-Host 'Complete pairing/readiness checks, then press Enter to start measurement' | Out-Null
    $auditStarted = [DateTimeOffset]::UtcNow

    if ($Mode -eq 'Soak') {
        Invoke-SamplingWindow -DurationSeconds ($SoakMinutes * 60)
    }
    elseif ($Mode -eq 'Reconnect') {
        $check = Wait-WpfRecovery -Cycle 0
        $recoveryChecks.Add($check)
        Confirm-WpfRecovery -Check $check
        Write-RecoveryEvidence
        for ($cycle = 1; $cycle -le $ReconnectCycles; $cycle++) {
            $currentRecoveryCycle = $cycle
            $previousEpoch = $check.BackendEpoch
            Write-RecoveryEvidence
            Write-Host "Reconnect cycle $cycle / $ReconnectCycles" -ForegroundColor Yellow
            Stop-OwnedBackend $backendProcess
            $backendProcess = Start-AuditBackend -Cycle $cycle
            $previousSamples.Remove('Backend')
            Wait-BackendReady $backendProcess
            $check = Wait-WpfRecovery -Cycle $cycle -PreviousBackendEpoch $previousEpoch
            $recoveryChecks.Add($check)
            Confirm-WpfRecovery -Check $check
            Write-RecoveryEvidence
        }
        $recoveryOutcome = 'RecoveredAndUserConfirmed'
        Write-RecoveryEvidence
    }
    else {
        Invoke-SamplingWindow -DurationSeconds 60
    }

    # Close WPF while the Backend is still available. Start-Process owns the
    # redirected log writer until the Backend exits, so stop the
    # helper-owned Backend before reading its optional counters.
    Stop-OwnedWpf $activeWpfProcess
    Stop-OwnedBackend $backendProcess
    $completionStatus = if ($Mode -eq 'Reconnect') { 'RecoveryVerifiedUserConfirmed' } else { 'CaptureCompleted' }
    Complete-SanitizedAudit -Status $completionStatus
    Write-Host 'Audit capture complete; helper-owned processes are closed.' -ForegroundColor Green
}
catch {
    $originalFailure = $_
    if ($Mode -eq 'Reconnect') { $recoveryOutcome = 'Failed' }
    try {
        Stop-OwnedBackend $backendProcess
    }
    catch {
        Write-Warning 'The helper-owned Backend could not be stopped cleanly during failure handling.'
    }
    try {
        Preserve-SanitizedFailureLogs
        Write-RecoveryEvidence
        if ($Mode -eq 'Reconnect' -and $stateCreated) {
            Complete-SanitizedAudit -Status 'Failed'
        }
    }
    catch {
        Write-Warning 'Sanitized failure logs could not be preserved; the original failure is retained.'
    }
    throw $originalFailure
}
finally {
    if ($recoveryOutcome -eq 'Running') {
        $recoveryOutcome = 'Aborted'
        try {
            Write-RecoveryEvidence
            Complete-SanitizedAudit -Status 'Aborted'
        }
        catch {
            Write-Warning 'Interrupted audit evidence could not be saved; this run is not a PASS.'
        }
    }
    [Environment]::SetEnvironmentVariable($wpfRecoveryDirectoryName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($wpfRecoveryRunName, $null, 'Process')
    Clear-BackendEnvironment
    Stop-OwnedWpf $activeWpfProcess
    Stop-OwnedBackend $backendProcess
    if ($null -ne $secureToken) {
        $secureToken.Dispose()
        $secureToken = $null
    }
    if ($stateCreated) { Remove-IsolatedState }
    Write-Host "$AuditLabel audit session cleaned up."
}
