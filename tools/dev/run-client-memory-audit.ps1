[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$TestAssembly,
    [Parameter(Mandatory=$true)][string]$AuditAssembly,
    [Parameter(Mandatory=$true)][string]$CaptureDirectory,
    [ValidateSet('Chat20','Mixed','GIF5','SalesMedia','GTA','SettingsClosed')][string]$Scenario='Chat20',
    [ValidateSet('heap','allocation')][string]$Mode='heap'
)
$ErrorActionPreference='Stop'
$testPath=(Resolve-Path -LiteralPath $TestAssembly).Path
$auditPath=(Resolve-Path -LiteralPath $AuditAssembly).Path
$capturePath=[IO.Path]::GetFullPath($CaptureDirectory)
if (Test-Path -LiteralPath $capturePath) { throw 'Use a new capture directory.' }
[IO.Directory]::CreateDirectory($capturePath) | Out-Null
$outputPath=Join-Path $capturePath 'synthetic.json'
$readyPath=$outputPath+'.owner-ready.json'
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=(Get-Command dotnet -ErrorAction Stop).Source
$start.Arguments='vstest "'+$testPath+'" --TestCaseFilter:FullyQualifiedName~ClientMemory22ProfileTests'
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.EnvironmentVariables['LSO_CLIENT_PROFILE']=$outputPath
$start.EnvironmentVariables['LSO_CLIENT_OWNER_SCENARIO']=$Scenario
$start.EnvironmentVariables['LSO_CLIENT_BFINAL']='1'
$start.EnvironmentVariables.Remove('LSO_CLIENT_VISUAL')
$start.EnvironmentVariables.Remove('LSO_CLIENT_EXTENDED')
$start.EnvironmentVariables.Remove('LSO_CLIENT_SALES_MEDIA')
$start.EnvironmentVariables.Remove('LSO_CLIENT_SOAK_SECONDS')
$owned=[Diagnostics.Process]::Start($start)
$stdout=$owned.StandardOutput.ReadToEndAsync()
$stderr=$owned.StandardError.ReadToEndAsync()
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(40)
    while (!(Test-Path -LiteralPath $readyPath) -and !$owned.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 150
    }
    if (!(Test-Path -LiteralPath $readyPath)) { throw 'Synthetic checkpoint was not reached.' }
    & $start.FileName $auditPath $readyPath $Mode (Join-Path $capturePath 'aggregate.json')
    if ($LASTEXITCODE -ne 0) { throw 'Ownership capture failed.' }
    if (!$owned.WaitForExit(30000)) { throw 'Synthetic testhost did not finish after collection.' }
    if ($owned.ExitCode -ne 0) { throw 'Synthetic testhost failed.' }
    Write-Output "PASS: $Scenario / $Mode -> $capturePath"
} finally {
    # Only the process this helper created; never find or terminate the user app by name.
    if (!$owned.HasExited) { $owned.Kill($true); $owned.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $capturePath 'test.stdout.log'),$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $capturePath 'test.stderr.log'),$stderr.GetAwaiter().GetResult())
    $owned.Dispose()
}
