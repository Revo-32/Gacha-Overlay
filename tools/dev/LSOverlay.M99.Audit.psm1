Set-StrictMode -Version Latest

$script:MaximumRetainedSamples = 21600
$script:IsWindowsAudit =
    [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function New-LsAuditAccumulator {
    param([int]$MaximumSamples = $script:MaximumRetainedSamples)

    if ($MaximumSamples -lt 2 -or $MaximumSamples -gt $script:MaximumRetainedSamples) {
        throw "MaximumSamples must be between 2 and $script:MaximumRetainedSamples."
    }

    return [pscustomobject]@{
        MaximumSamples = $MaximumSamples
        Samples = [System.Collections.Generic.List[object]]::new()
    }
}

function Add-LsAuditSample {
    param(
        [Parameter(Mandatory = $true)]$Accumulator,
        [Parameter(Mandatory = $true)]$Sample)

    while ($Accumulator.Samples.Count -ge $Accumulator.MaximumSamples) {
        $Accumulator.Samples.RemoveAt(0)
    }
    $Accumulator.Samples.Add($Sample)
}

function Get-LsAuditTrend {
    param([double[]]$Values)

    $finite = @($Values | Where-Object {
        -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_)
    })
    if ($finite.Count -eq 0) {
        return [ordered]@{
            First20Average = $null
            Last20Average = $null
            Difference = $null
            PercentDifference = $null
            Classification = 'Unavailable'
        }
    }

    $window = [Math]::Max(1, [Math]::Ceiling($finite.Count * 0.2))
    $first = [double](($finite | Select-Object -First $window | Measure-Object -Average).Average)
    $last = [double](($finite | Select-Object -Last $window | Measure-Object -Average).Average)
    $difference = $last - $first
    $percent = if ([Math]::Abs($first) -lt 0.000001) {
        if ([Math]::Abs($difference) -lt 0.000001) { 0.0 } else { $null }
    }
    else {
        ($difference / $first) * 100.0
    }

    $classification = 'Stable'
    if ($difference -gt 64 -and ($null -eq $percent -or $percent -gt 25)) {
        $classification = 'Needs Investigation'
    }
    elseif ($difference -gt 16 -and ($null -eq $percent -or $percent -gt 10)) {
        $classification = 'Possible Growth'
    }

    return [ordered]@{
        First20Average = [Math]::Round($first, 3)
        Last20Average = [Math]::Round($last, 3)
        Difference = [Math]::Round($difference, 3)
        PercentDifference = if ($null -eq $percent) { $null } else { [Math]::Round($percent, 3) }
        Classification = $classification
    }
}

function Get-LsPercentile {
    param(
        [double[]]$Values,
        [double]$Percentile)

    $finite = @($Values | Where-Object {
        -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_)
    } | Sort-Object)
    if ($finite.Count -eq 0) {
        return $null
    }

    $index = [int]([Math]::Ceiling($Percentile * $finite.Count) - 1)
    $index = [Math]::Max(0, [Math]::Min($finite.Count - 1, $index))
    return [Math]::Round([double]$finite[$index], 3)
}

function Get-LsProcessAuditSample {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][ValidateSet('WPF', 'Backend')][string]$Kind,
        $Previous)

    $Process.Refresh()
    $captured = [DateTimeOffset]::UtcNow
    $cpuTime = $Process.TotalProcessorTime.TotalSeconds
    $cpuPercent = $null
    if ($null -ne $Previous) {
        $wall = ($captured - [DateTimeOffset]$Previous.CapturedAt).TotalSeconds
        $cpu = $cpuTime - [double]$Previous.TotalCpuSeconds
        if ($wall -gt 0 -and $cpu -ge 0) {
            $cpuPercent = [Math]::Max(
                0,
                ($cpu / ($wall * [Environment]::ProcessorCount)) * 100.0)
        }
    }

    $gdi = $null
    $user = $null
    if ($script:IsWindowsAudit -and ('LsOverlayAuditNativeMethods' -as [type])) {
        $gdiValue = [LsOverlayAuditNativeMethods]::GetGuiResources($Process.Handle, 0)
        $userValue = [LsOverlayAuditNativeMethods]::GetGuiResources($Process.Handle, 1)
        if ($gdiValue -gt 0) { $gdi = [int]$gdiValue }
        if ($userValue -gt 0) { $user = [int]$userValue }
    }

    return [pscustomobject][ordered]@{
        TimestampUtc = $captured.ToString('O')
        Process = $Kind
        CpuPercent = if ($null -eq $cpuPercent) { $null } else { [Math]::Round($cpuPercent, 3) }
        WorkingSetMiB = [Math]::Round($Process.WorkingSet64 / 1MB, 3)
        PrivateBytesMiB = [Math]::Round($Process.PrivateMemorySize64 / 1MB, 3)
        Handles = $(try { $Process.HandleCount } catch { $null })
        Threads = $(try { $Process.Threads.Count } catch { $null })
        GdiObjects = $gdi
        UserObjects = $user
        CapturedAt = $captured
        TotalCpuSeconds = $cpuTime
    }
}

function Get-LsProcessAuditSummary {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Samples,
        [Parameter(Mandatory = $true)][ValidateSet('WPF', 'Backend')][string]$Kind)

    $selected = @($Samples | Where-Object { $_.Process -eq $Kind })
    if ($selected.Count -eq 0) {
        return [ordered]@{ SampleCount = 0; Status = 'NotRun' }
    }

    function Get-Range([object[]]$items, [string]$property) {
        $values = @($items | ForEach-Object { $_.$property } | Where-Object { $null -ne $_ })
        if ($values.Count -eq 0) { return $null }
        return [ordered]@{
            Average = [Math]::Round([double](($values | Measure-Object -Average).Average), 3)
            Minimum = [Math]::Round([double](($values | Measure-Object -Minimum).Minimum), 3)
            Maximum = [Math]::Round([double](($values | Measure-Object -Maximum).Maximum), 3)
        }
    }

    $cpu = @($selected.CpuPercent | Where-Object { $null -ne $_ })
    return [ordered]@{
        SampleCount = $selected.Count
        CpuPercent = [ordered]@{
            Average = if ($cpu.Count -eq 0) { $null } else {
                [Math]::Round([double](($cpu | Measure-Object -Average).Average), 3)
            }
            P95 = Get-LsPercentile -Values $cpu -Percentile 0.95
        }
        WorkingSetMiB = Get-Range $selected 'WorkingSetMiB'
        PrivateBytesMiB = Get-Range $selected 'PrivateBytesMiB'
        Handles = Get-Range $selected 'Handles'
        Threads = Get-Range $selected 'Threads'
        GdiObjects = Get-Range $selected 'GdiObjects'
        UserObjects = Get-Range $selected 'UserObjects'
        WorkingSetTrend = Get-LsAuditTrend -Values @($selected.WorkingSetMiB)
        PrivateBytesTrend = Get-LsAuditTrend -Values @($selected.PrivateBytesMiB)
        ManagedHeap = 'Unavailable from low-overhead external process sampling'
    }
}

function Test-LsAuditPayload {
    param([Parameter(Mandatory = $true)][string]$Json)

    $forbidden = @(
        '(?i)authorization\s*:',
        '(?i)bearer\s+[A-Za-z0-9._~-]+',
        '(?i)(bot|access|refresh)[_-]?token',
        '(?i)pairing(claim)?secret',
        '(?i)message(body|content)',
        '(?i)sales(body|content)',
        '(?<!\d)\d{17,20}(?!\d)')
    foreach ($pattern in $forbidden) {
        if ($Json -match $pattern) {
            throw 'Audit payload contains a forbidden secret or content category.'
        }
    }

    return $true
}

function Write-LsAuditOutputs {
    param(
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Samples)

    [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $json = $Summary | ConvertTo-Json -Depth 12
    Test-LsAuditPayload -Json $json | Out-Null
    $summaryPath = Join-Path $OutputDirectory 'm99-soak-summary.json'
    [System.IO.File]::WriteAllText(
        $summaryPath,
        $json,
        [System.Text.UTF8Encoding]::new($false))

    $metricsPath = Join-Path $OutputDirectory 'm99-soak-metrics.csv'
    if ($Samples.Count -eq 0) {
        [System.IO.File]::WriteAllText($metricsPath,
            'TimestampUtc,Process,CpuPercent,WorkingSetMiB,PrivateBytesMiB,Handles,Threads,GdiObjects,UserObjects' + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))
    }
    else {
        $Samples | Select-Object TimestampUtc, Process, CpuPercent, WorkingSetMiB,
            PrivateBytesMiB, Handles, Threads, GdiObjects, UserObjects |
            Export-Csv -LiteralPath $metricsPath -NoTypeInformation -Encoding UTF8
    }
    return [pscustomobject]@{ SummaryPath = $summaryPath; MetricsPath = $metricsPath }
}

function Get-LsWpfRecoveryState {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][int]$ExpectedProcessId,
        [string]$PreviousBackendEpoch = '',
        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)

    $result = [pscustomobject]@{
        Ready = $false; Reason = 'EvidenceMissing'; Fatal = $false
        Attempt = 0L; BackendEpoch = ''; IdentityValid = $false
    }
    if ($null -eq $Report) { return $result }
    try {
        if ($Report.Schema -ne 'LSOverlay.WpfRecovery.v1' -or
            $Report.RunId -cne $RunId -or $Report.ProcessId -ne $ExpectedProcessId) {
            $result.Reason = 'EvidenceIdentityMismatch'
            return $result
        }
        $age = ($Now - [DateTimeOffset]$Report.ObservedAtUtc).TotalSeconds
        if ($age -lt -2 -or $age -gt 5) {
            $result.Reason = 'EvidenceStale'
            return $result
        }
        $flags = @('SalesTrackingEnabled', 'ChatSnapshotApplied', 'ChatStreamReady',
            'PresenceSnapshotApplied', 'PresenceStreamLive', 'SalesSnapshotComplete',
            'SalesStreamReady', 'AuthenticationRequired', 'TerminalFailure', 'AttemptEnded')
        foreach ($flag in $flags) {
            if ($Report.$flag -isnot [bool]) {
                $result.Reason = 'EvidenceInvalid'
                return $result
            }
        }
        $result.IdentityValid = $true
        $result.Attempt = [long]$Report.Attempt
        if ($Report.AuthenticationRequired -or $Report.TerminalFailure) {
            $result.Fatal = $true
            $result.Reason = if ($Report.AuthenticationRequired) { 'AuthenticationRequired' } else { 'RecoveryFailed' }
            return $result
        }
        if ($Report.AttemptEnded -or $result.Attempt -lt 1) {
            $result.Reason = 'ConnectionAttemptPending'
            return $result
        }
        if ($Report.BackendEpoch -cnotmatch '^[A-F0-9]{8}(-[A-F0-9]{8}){7}$') {
            $result.Reason = 'PresenceBootstrapPending'
            return $result
        }
        $result.BackendEpoch = [string]$Report.BackendEpoch
        if ($PreviousBackendEpoch -and $result.BackendEpoch -eq $PreviousBackendEpoch) {
            $result.Reason = 'WaitingForNewBackend'
            return $result
        }
        $missing = @($flags | Select-Object -First 7 | Where-Object { -not $Report.$_ })
        if ($missing.Count -gt 0) {
            $result.Reason = $missing -join ','
            return $result
        }
        $result.Ready = $true
        $result.Reason = 'Ready'
        return $result
    }
    catch {
        $result.Ready = $false
        $result.Reason = 'EvidenceInvalid'
        return $result
    }
}

function New-LsRecoveryGate {
    [pscustomobject]@{ StableSince = $null; Attempt = 0L; BackendEpoch = '' }
}

function Update-LsRecoveryGate {
    param(
        [Parameter(Mandatory = $true)]$Gate,
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][double]$ElapsedSeconds,
        [double]$StableSeconds = 10,
        [double]$TimeoutSeconds = 120)

    if ($State.Fatal) { return 'Failed' }
    if ($ElapsedSeconds -ge $TimeoutSeconds) { return 'Timeout' }
    if (-not $State.Ready) {
        $Gate.StableSince = $null
        return 'Waiting'
    }
    if ($null -eq $Gate.StableSince -or $Gate.Attempt -ne $State.Attempt -or
        $Gate.BackendEpoch -ne $State.BackendEpoch) {
        $Gate.StableSince = $ElapsedSeconds
        $Gate.Attempt = $State.Attempt
        $Gate.BackendEpoch = $State.BackendEpoch
    }
    if (($ElapsedSeconds - $Gate.StableSince) -ge $StableSeconds) { return 'Ready' }
    return 'Stabilizing'
}

if ($script:IsWindowsAudit -and -not ('LsOverlayAuditNativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LsOverlayAuditNativeMethods
{
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@
}

Export-ModuleMember -Function New-LsAuditAccumulator, Add-LsAuditSample,
    Get-LsAuditTrend, Get-LsProcessAuditSample, Get-LsProcessAuditSummary,
    Test-LsAuditPayload, Write-LsAuditOutputs, Get-LsWpfRecoveryState,
    New-LsRecoveryGate, Update-LsRecoveryGate
