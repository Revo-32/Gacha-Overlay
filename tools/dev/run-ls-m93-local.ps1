# Developer-only probe against an existing Backend. No isolated registry or new login.
[CmdletBinding()]
param([string]$BackendUrl = 'https://overlay.revo32.cloud')
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$probeProject = Join-Path $repositoryRoot 'src/LSOverlay.TransportProbe/LSOverlay.TransportProbe.csproj'
$tokenName = 'LSO_DISCORD_BOT_TOKEN'
$secretNames = @($tokenName, 'LSO_DISCORD_OAUTH_CLIENT_SECRET')
foreach ($name in $secretNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
$secureCredential = Read-Host 'Existing Remote credential (developer-only; never a Bot Token)' -AsSecureString
$pointer = [IntPtr]::Zero
$plain = $null
try {
    if ($secureCredential.Length -eq 0) { throw 'An existing Remote credential is required. New authentication uses WPF browser login.' }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureCredential)
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    [Environment]::SetEnvironmentVariable('LSO_PROBE_ACCESS_TOKEN', $plain, 'Process')
    [Environment]::SetEnvironmentVariable($tokenName, $null, 'Process')
    & dotnet run --project $probeProject --no-launch-profile -- $BackendUrl
    if ($LASTEXITCODE -ne 0) { throw 'Transport probe failed. No credentials were printed.' }
} finally {
    [Environment]::SetEnvironmentVariable('LSO_PROBE_ACCESS_TOKEN', $null, 'Process')
    $plain = $null
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $secureCredential.Dispose()
}
