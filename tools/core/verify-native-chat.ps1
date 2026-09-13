[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $repository 'artifacts/core/m3/fixture-publish/LSOverlay.CoreFixtureHost.dll'
$client = Join-Path $repository 'artifacts/core/m3/native/Release/LSOverlayCore.exe'
$source = Join-Path $repository 'artifacts/core/m3/chat-fixtures'
$report = Join-Path $repository ('artifacts/core/m3/runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
& 'E:\CODEX\Tools\dotnet8\dotnet.exe' $fixture --export-chat $source
if ($LASTEXITCODE -ne 0) { throw 'Synthetic canonical chat export failed.' }
$arguments = @('--no-hotkeys','--chat-fixture',('"' + (Join-Path $source 'chat20.json') + '"'),'--chat-verify',('"' + $report + '"'))
$process = Start-Process -FilePath $client -ArgumentList $arguments -WorkingDirectory (Split-Path $client) -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(45000)) { throw 'Native chat verification timeout.' }
    if ($process.ExitCode -ne 0) {
        $errorFile = Join-Path $report 'native-shell-error.txt'
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile }
        throw "Native chat verification failed: $report"
    }
    Write-Output $report
    Get-Content -LiteralPath (Join-Path $report 'native-chat-metrics.json')
} finally {
    # Only this verification runner's own process, never the user's Full app.
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
