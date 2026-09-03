[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PublishedExe)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishedExe).Path
if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or [IO.Path]::GetFileName($source) -ne 'GachaOverlay.App.exe') {
    throw 'Select the published GachaOverlay.App.exe.'
}

# No build, Backend, Bot Token, Host IDs, real profile, or network connection.
# Copy only the self-contained single EXE, then run outside the repository.
$checkRoot = Join-Path ([IO.Path]::GetTempPath()) ('GachaOverlay-M9141-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot | Out-Null
$copiedExe = Join-Path $checkRoot 'GachaOverlay.App.exe'
Copy-Item -LiteralPath $source -Destination $copiedExe
$outputPath = Join-Path $checkRoot 'output'
$arguments = @('--verify-client-export', ('"' + $outputPath + '"'))
$process = Start-Process -FilePath $copiedExe -ArgumentList $arguments -WorkingDirectory $checkRoot -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(45000)) {
        throw "Offline check timed out; helper-owned PID=$($process.Id). Evidence: $checkRoot"
    }
    if ($process.ExitCode -ne 0) { throw "Offline check failed (exit=$($process.ExitCode)). Evidence: $checkRoot" }
    $resultPath = Join-Path $outputPath 'result.json'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.Status -ne 'PASS' -or -not $result.SingleFile -or $result.PendingTemporaryFiles -ne 0) {
        throw "The published single-file check did not pass. Evidence: $checkRoot"
    }
    Write-Host "M9.14.1 offline published-client check PASS: $resultPath"
    Write-Host 'In-game physical input and real-user diagnostic export still require user validation.'
} finally {
    $process.Dispose()
}
