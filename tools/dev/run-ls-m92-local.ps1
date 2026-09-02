[CmdletBinding()]
param(
    [string]$GuildId,
    [string]$TrackedHostIds,
    [string]$BackendUrl = 'http://127.0.0.1:5188'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$backendProject = Join-Path $repositoryRoot 'src\LSOverlay.Backend\LSOverlay.Backend.csproj'
$probeProject = Join-Path $repositoryRoot 'src\LSOverlay.TransportProbe\LSOverlay.TransportProbe.csproj'
$stateDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("LSOverlay-M92-" + [Guid]::NewGuid().ToString('N'))
$shutdownFile = Join-Path $stateDirectory 'shutdown.request'
$stdoutFile = Join-Path $stateDirectory 'backend.stdout.log'
$stderrFile = Join-Path $stateDirectory 'backend.stderr.log'
$tokenName = 'LSO_DISCORD_BOT_TOKEN'
$guildName = 'LSO_DISCORD_GUILD_ID'
$hostsName = 'LSO_TRACKED_HOST_IDS'
$stateName = 'LSO_STATE_DIRECTORY'
$listenName = 'LSO_LISTEN_URL'
$shutdownName = 'LSO_DEV_SHUTDOWN_FILE'
$backendProcess = $null
$tokenPointer = [IntPtr]::Zero
$plainToken = $null

try {
    if (-not $PSBoundParameters.ContainsKey('GuildId')) {
        $GuildId = Read-Host 'Target Discord Guild ID'
    }
    if (-not $PSBoundParameters.ContainsKey('TrackedHostIds')) {
        $TrackedHostIds = Read-Host 'Tracked Host Discord User ID(s) [optional]'
    }

    $secureToken = Read-Host 'Discord Bot Token' -AsSecureString
    New-Item -ItemType Directory -Path $stateDirectory | Out-Null
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    [Environment]::SetEnvironmentVariable($tokenName, $plainToken, 'Process')
    [Environment]::SetEnvironmentVariable($guildName, $GuildId, 'Process')
    [Environment]::SetEnvironmentVariable($hostsName, $TrackedHostIds, 'Process')
    [Environment]::SetEnvironmentVariable($stateName, $stateDirectory, 'Process')
    [Environment]::SetEnvironmentVariable($listenName, $BackendUrl, 'Process')
    [Environment]::SetEnvironmentVariable($shutdownName, $shutdownFile, 'Process')

    $backendProcess = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', $backendProject, '--no-launch-profile') `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutFile `
        -RedirectStandardError $stderrFile `
        -PassThru

    [Environment]::SetEnvironmentVariable($tokenName, $null, 'Process')
    $plainToken = $null
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
        $tokenPointer = [IntPtr]::Zero
    }
    $secureToken.Dispose()

    $ready = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if ($backendProcess.HasExited) {
            throw "Backend exited before the local transport endpoint became ready."
        }
        try {
            $health = Invoke-RestMethod -Uri ($BackendUrl.TrimEnd('/') + '/healthz') -TimeoutSec 1
            if ($health.status -eq 'ok') {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) {
        throw 'Backend health endpoint did not become ready within 30 seconds.'
    }

    Write-Host 'Backend: HTTP Ready'
    $discordReady = $false
    for ($attempt = 0; $attempt -lt 240; $attempt++) {
        if ($backendProcess.HasExited) {
            throw 'Backend exited before Discord pairing became ready.'
        }
        if ((Test-Path -LiteralPath $stdoutFile) -and
            (Select-String -LiteralPath $stdoutFile `
                -Pattern 'Discord pairing command: Available' `
                -Quiet)) {
            $discordReady = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $discordReady) {
        throw 'Discord pairing command did not become ready within 60 seconds.'
    }

    Write-Host 'Discord: Ready'
    & dotnet run --project $probeProject --no-launch-profile -- $BackendUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Transport Probe exited with code $LASTEXITCODE."
    }
}
finally {
    [Environment]::SetEnvironmentVariable($tokenName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($guildName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($hostsName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($stateName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($listenName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($shutdownName, $null, 'Process')
    $plainToken = $null
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
    if ($backendProcess -and -not $backendProcess.HasExited) {
        [System.IO.File]::WriteAllText($shutdownFile, 'stop')
        if (-not $backendProcess.WaitForExit(10000)) {
            Stop-Process -Id $backendProcess.Id -Force
        }
    }
    if (Test-Path -LiteralPath $stateDirectory) {
        Remove-Item -LiteralPath $stateDirectory -Recurse -Force
    }
}
