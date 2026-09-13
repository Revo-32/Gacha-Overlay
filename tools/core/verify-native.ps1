[CmdletBinding()]
param(
    [ValidateRange(2,300)][int]$PhaseSeconds = 15,
    [switch]$NoHotkeys,
    [ValidateSet('m1','m2')][string]$Stage = 'm2'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$executable = Join-Path $repository "artifacts/core/$Stage/native/Release/LSOverlayCore.exe"
if (!(Test-Path -LiteralPath $executable)) { throw 'Run tools/core/build-native.ps1 first.' }
$outputDirectory = Join-Path $repository ("artifacts/core/$Stage/runs/" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $outputDirectory -ErrorAction Stop | Out-Null
$arguments = @('--verify', ('"' + $outputDirectory + '"'), '--phase-seconds', $PhaseSeconds)
if ($NoHotkeys) { $arguments += '--no-hotkeys' }
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory (Split-Path $executable) -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(($PhaseSeconds * 3 + 90) * 1000)) {
        # This is the process created above, never an existing Full/Core process.
        $process.Kill()
        throw 'Native verification timed out; only its own process was stopped.'
    }
    $exitCode = $process.ExitCode
} finally { $process.Dispose() }
$errorFile = Join-Path $outputDirectory 'native-shell-error.txt'
if ($exitCode -ne 0) {
    if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile }
    throw "Native verification failed (exit $exitCode): $outputDirectory"
}
$metrics = Get-Content -LiteralPath (Join-Path $outputDirectory 'native-shell-metrics.json') -Raw | ConvertFrom-Json
if ($metrics.phases.Count -ne 3 -or @($metrics.phases | Where-Object idleRenders -ne 0).Count -ne 0) {
    throw 'Native idle rendering gate failed.'
}
$sourceHead = & git -C $repository rev-parse HEAD
$sourceStatus = @(& git -C $repository status --porcelain)
$metadata = [ordered]@{
    executable = $executable
    sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    sourceHead = $sourceHead
    sourceWorktreeClean = ($sourceStatus.Count -eq 0)
    sourceChangedPaths = $sourceStatus
    os = [Environment]::OSVersion.VersionString
    logicalProcessors = [Environment]::ProcessorCount
    utc = [DateTime]::UtcNow.ToString('O')
    scope = 'M1 native shell only; no live Discord or Full comparison'
}
# Generated measurement metadata only; this is not a settings or credential file.
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputDirectory 'build-and-machine.json') -Encoding UTF8
Write-Output $outputDirectory
$metrics.phases | ForEach-Object {
    $private = $_.samples.privateBytes | Measure-Object -Average -Minimum -Maximum
    $working = $_.samples.workingSet | Measure-Object -Average -Minimum -Maximum
    [pscustomobject]@{
        Phase = $_.name
        Seconds = $_.seconds
        PrivateMiB = [math]::Round($private.Average / 1MB,2)
        PrivateRangeMiB = ('{0:N2}..{1:N2}' -f ($private.Minimum / 1MB), ($private.Maximum / 1MB))
        WorkingSetMiB = [math]::Round($working.Average / 1MB,2)
        CpuOneCorePercent = $_.cpuOneCorePercent
        IdleRenders = $_.idleRenders
    }
} | Format-Table -AutoSize
