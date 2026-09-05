[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CaptureDirectory,
    [string]$BaselinePrefix = 'B0',
    [string]$CandidatePrefix = 'Final',
    [ValidateRange(3, 20)][int]$Repeats = 3
)

$ErrorActionPreference = 'Stop'
function Median($values) {
    $ordered = @($values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

$variants = @{}
foreach ($prefix in @($BaselinePrefix, $CandidatePrefix)) {
    $runs = @(for ($index = 1; $index -le $Repeats; $index++) {
        $path = Join-Path $CaptureDirectory "$prefix-run-$index.json"
        $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($data.results.Count -ne 14 -or $data.results[-1].scenario -ne 'Cleanup') {
            throw "Incomplete 14-scenario capture: $path"
        }
        $data
    })
    $variants[$prefix] = $runs
}

foreach ($scenario in $variants[$BaselinePrefix][0].results.scenario) {
    $row = [ordered]@{ Scenario = $scenario }
    foreach ($prefix in @($BaselinePrefix, $CandidatePrefix)) {
        $samples = @($variants[$prefix] | ForEach-Object { $_.results | Where-Object scenario -eq $scenario })
        if ($samples.Count -ne $Repeats) { throw 'Missing or duplicate scenario.' }
        $row[$prefix] = [ordered]@{
            PrivateMiB = [Math]::Round((Median @($samples.privateBytes)) / 1MB, 2)
            WorkingSetMiB = [Math]::Round((Median @($samples.workingSet)) / 1MB, 2)
            ManagedMiB = [Math]::Round((Median @($samples.managedHeap)) / 1MB, 2)
            CpuCorePercent = [Math]::Round((Median @($samples | ForEach-Object { $_.cpuMs / $_.seconds / 10 })), 2)
            AllocationMiBPerSecond = [Math]::Round((Median @($samples | ForEach-Object { $_.allocatedBytes / $_.seconds / 1MB })), 3)
            DispatcherP95Ms = [Math]::Round((Median @($samples.uiP95Ms)), 3)
        }
    }
    [pscustomobject]$row
}
