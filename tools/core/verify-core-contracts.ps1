[CmdletBinding()]
param([switch]$IncludeGui, [ValidateSet('m2','m3')][string]$Stage = 'm2')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = 'E:\CODEX\Tools\dotnet8\dotnet.exe'
$fixture = Join-Path $repository "artifacts/core/$Stage/fixture-publish/LSOverlay.CoreFixtureHost.dll"
$probe = Join-Path $repository "artifacts/core/$Stage/native/Release/LSOverlayCoreTransportProbe.exe"
$wireTest = Join-Path $repository "artifacts/core/$Stage/native/Release/LSOverlayCoreWireTests.exe"
$wireFixture = Join-Path $repository "artifacts/core/$Stage/wire-fixture.ndjson"
foreach ($required in @($dotnet,$fixture,$probe,$wireTest)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required build output missing: $required" }
}
& $dotnet $fixture --export-wire $wireFixture
if ($LASTEXITCODE -ne 0) { throw 'Cross-language fixture export failed.' }
& $wireTest $wireFixture
if ($LASTEXITCODE -ne 0) { throw 'C# to native lossless wire validation failed.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$origin = "http://127.0.0.1:$port"
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $dotnet
$mode = if ($Stage -eq 'm3') { '--m3-chat-fixture' } else { '--m2-contract-fixture' }
$startInfo.Arguments = '"' + $fixture + '" ' + $mode + ' --urls ' + $origin
$startInfo.WorkingDirectory = Split-Path $fixture
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.EnvironmentVariables['CORE_FIXTURE_ORIGIN'] = $origin
$fixtureProcess = [Diagnostics.Process]::new()
$fixtureProcess.StartInfo = $startInfo
try {
    if (!$fixtureProcess.Start()) { throw 'Could not start isolated fixture process.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($fixtureProcess.HasExited) { throw 'Fixture process exited before readiness.' }
        try {
            $manifest = Invoke-RestMethod -Uri "$origin/fixture/manifest" -TimeoutSec 1
            if ($manifest.syntheticAuthentication -eq $true -and $manifest.liveDiscord -eq $false) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (!$ready) { throw 'Isolated fixture readiness failed.' }
    & $probe $origin
    if ($LASTEXITCODE -ne 0) { throw 'Native HTTP/WebSocket contract validation failed.' }
    $final = Invoke-RestMethod -Uri "$origin/fixture/manifest" -TimeoutSec 3
    [pscustomobject]@{Scope='synthetic contract fixture only';Connections=$final.connections;Snapshots=$final.snapshots;Resumes=$final.resumes;AuthStarts=$final.authStarts} | ConvertTo-Json
    if ($IncludeGui) {
        $client = Join-Path $repository "artifacts/core/$Stage/native/Release/LSOverlayCore.exe"
        $report = Join-Path $repository "artifacts/core/$Stage/local-gui"
        $gui = Start-Process -FilePath $client -ArgumentList @('--no-hotkeys','--fixture-endpoint',$origin,'--fixture-verify',('"' + $report + '"')) -WindowStyle Hidden -PassThru
        try {
            if (!$gui.WaitForExit(30000)) { throw 'Synthetic GUI verification exceeded 30 seconds.' }
            if ($gui.ExitCode -ne 0) { throw 'Synthetic GUI verification failed.' }
            Get-Content -LiteralPath (Join-Path $report 'native-synthetic-connected.json')
        } finally {
            if (!$gui.HasExited) { $gui.Kill(); $gui.WaitForExit(5000) | Out-Null }
            $gui.Dispose()
        }
    }
} finally {
    # Only this runner's own ephemeral child, not any Full/Core user process.
    if (!$fixtureProcess.HasExited) { $fixtureProcess.Kill(); $fixtureProcess.WaitForExit(5000) | Out-Null }
    $fixtureProcess.Dispose()
}
