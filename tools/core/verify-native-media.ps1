[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$report = Join-Path $repository ('artifacts/core/m4/runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build-native.ps1') -Stage m4 }
& 'E:\CODEX\Tools\dotnet8\dotnet.exe' run --project (Join-Path $PSScriptRoot 'LSOverlay.CoreMediaProbe') -c Release -- --verify $report
if ($LASTEXITCODE -ne 0) { throw 'Server media focused verification failed.' }
$client = Join-Path $repository 'artifacts/core/m4/native/Release/LSOverlayCore.exe'
$fixture = Join-Path $report 'native'
$guiReport = Join-Path $report 'native-gui'
$arguments = @('--no-hotkeys','--chat-fixture',('"' + (Join-Path $fixture 'chat.json') + '"'),'--media-fixture',('"' + (Join-Path $fixture 'media.json') + '"'),'--media-verify',('"' + $guiReport + '"'))
$process = Start-Process -FilePath $client -ArgumentList $arguments -WorkingDirectory (Split-Path $client) -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(30000)) { throw 'Native media verification timed out.' }
    if ($process.ExitCode -ne 0) {
        $errorFile = Join-Path $guiReport 'native-shell-error.txt'
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile }
        throw 'Native media verification failed.'
    }
    Get-Content -LiteralPath (Join-Path $guiReport 'native-media-checks.json')
    Get-Content -LiteralPath (Join-Path $guiReport 'native-media-visible-metrics.json')
    Write-Output $report
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
