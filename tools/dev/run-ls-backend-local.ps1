[CmdletBinding()]
param(
    [string]$GuildId,
    [string]$TrackedHostIds
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $repositoryRoot 'src\LSOverlay.Backend\LSOverlay.Backend.csproj'
$tokenName = 'LSO_DISCORD_BOT_TOKEN'
$guildName = 'LSO_DISCORD_GUILD_ID'
$hostsName = 'LSO_TRACKED_HOST_IDS'
$secureToken = $null
$credential = $null
$plainToken = $null
$backendExitCode = 1

try {
    if ([string]::IsNullOrWhiteSpace($GuildId)) {
        $GuildId = Read-Host 'Target Discord Guild ID'
    }

    if (-not $PSBoundParameters.ContainsKey('TrackedHostIds')) {
        $TrackedHostIds = Read-Host 'Tracked Host Discord User ID(s) [optional]'
    }

    $secureToken = Read-Host 'Discord Bot Token' -AsSecureString
    $credential = [System.Net.NetworkCredential]::new('', $secureToken)
    $plainToken = $credential.Password
    [Environment]::SetEnvironmentVariable($tokenName, $plainToken, 'Process')
    [Environment]::SetEnvironmentVariable($guildName, $GuildId, 'Process')
    [Environment]::SetEnvironmentVariable($hostsName, $TrackedHostIds, 'Process')

    & dotnet run --project $projectPath --configuration Release --no-launch-profile
    $backendExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable($tokenName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($guildName, $null, 'Process')
    [Environment]::SetEnvironmentVariable($hostsName, $null, 'Process')
    $plainToken = $null
    $credential = $null
    if ($null -ne $secureToken) {
        $secureToken.Dispose()
    }
}

if ($backendExitCode -ne 0) {
    throw "LS Overlay Backend exited with code $backendExitCode."
}
