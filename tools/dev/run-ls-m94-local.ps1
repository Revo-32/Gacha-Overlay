<#
.SYNOPSIS
Starts an isolated M9.4 local Backend and the real Release WPF application.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\dev\run-ls-m94-local.ps1"
#>
[CmdletBinding()]
param(
    [string]$GuildId,
    [string]$TrackedHostIds,
    [string]$SessionHost1Id,
    [string]$SessionHost2Id,
    [string]$BackendUrl = 'http://127.0.0.1:5188',
    [ValidateSet('M9.4', 'M9.5', 'M9.6', 'M9.7', 'M9.8', 'M9.8.1')]
    [string]$ValidationMilestone = 'M9.4'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$backendProject = Join-Path $repositoryRoot 'src\LSOverlay.Backend\LSOverlay.Backend.csproj'
$wpfProject = Join-Path $repositoryRoot 'src\GachaOverlay.App\GachaOverlay.App.csproj'
$solutionPath = Join-Path $repositoryRoot 'GachaOverlay.sln'
$publishProfile = Join-Path $repositoryRoot 'src\GachaOverlay.App\Properties\PublishProfiles\win-x64-singlefile.pubxml'
$statePrefix = if ($ValidationMilestone -eq 'M9.8.1') {
    'LSOverlay-M981-'
}
elseif ($ValidationMilestone -eq 'M9.8') {
    'LSOverlay-M98-'
}
elseif ($ValidationMilestone -eq 'M9.7') {
    'LSOverlay-M97-'
}
elseif ($ValidationMilestone -eq 'M9.6') {
    'LSOverlay-M96-'
}
elseif ($ValidationMilestone -eq 'M9.5') {
    'LSOverlay-M95-'
}
else {
    'LSOverlay-M94-'
}
$stateDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
    $statePrefix + [Guid]::NewGuid().ToString('N'))
$backendOutput = Join-Path $stateDirectory 'backend-release'
$wpfOutput = Join-Path $stateDirectory 'wpf-release'
$backendStdout = Join-Path $stateDirectory 'backend.stdout.log'
$backendStderr = Join-Path $stateDirectory 'backend.stderr.log'
$failureLogLeaf = if ($ValidationMilestone -eq 'M9.8.1') {
    'LSOverlay-M981-LastFailure'
}
elseif ($ValidationMilestone -eq 'M9.8') {
    'LSOverlay-M98-LastFailure'
}
elseif ($ValidationMilestone -eq 'M9.7') {
    'LSOverlay-M97-LastFailure'
}
elseif ($ValidationMilestone -eq 'M9.6') {
    'LSOverlay-M96-LastFailure'
}
elseif ($ValidationMilestone -eq 'M9.5') {
    'LSOverlay-M95-LastFailure'
}
else {
    'LSOverlay-M94-LastFailure'
}
$failureLogDirectory = Join-Path ([System.IO.Path]::GetTempPath()) $failureLogLeaf
$shutdownFile = Join-Path $stateDirectory 'shutdown.request'
$tokenName = 'LSO_DISCORD_BOT_TOKEN'
$guildName = 'LSO_DISCORD_GUILD_ID'
$hostsName = 'LSO_TRACKED_HOST_IDS'
$host1Name = 'LSO_SESSION_HOST_1_ID'
$host2Name = 'LSO_SESSION_HOST_2_ID'
$stateName = 'LSO_STATE_DIRECTORY'
$listenName = 'LSO_LISTEN_URL'
$shutdownName = 'LSO_DEV_SHUTDOWN_FILE'
$backendEnvironmentNames = @(
    $tokenName,
    $guildName,
    $hostsName,
    $host1Name,
    $host2Name,
    $stateName,
    $listenName,
    $shutdownName)
$secureToken = $null
$tokenPointer = [IntPtr]::Zero
$plainToken = $null
$backendProcess = $null
$activeWpfProcess = $null
$stateDirectoryCreated = $false

function Assert-RequiredPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required repository path is missing: $Path"
    }
}

function Normalize-DiscordId {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name)

    [UInt64]$parsed = 0
    $normalized = $Value.Trim()
    if (-not [UInt64]::TryParse($normalized, [ref]$parsed) -or $parsed -eq 0) {
        throw "$Name must be a valid Discord ID."
    }

    return $parsed.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Normalize-TrackedHostIds {
    param([AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    $normalized = New-Object System.Collections.Generic.List[string]
    $unique = New-Object System.Collections.Generic.HashSet[string]
    foreach ($segment in ($Value -split '[,;]')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $id = Normalize-DiscordId -Value $segment -Name 'Tracked Host Discord User ID'
        if ($unique.Add($id)) {
            $normalized.Add($id)
        }
        else {
            throw 'Legacy tracked Host configuration contains duplicate Discord IDs.'
        }
    }

    if ($normalized.Count -gt 2) {
        throw 'Legacy tracked Host configuration supports at most 2 unique IDs.'
    }

    return ($normalized -join ',')
}

function Normalize-OptionalDiscordId {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    return Normalize-DiscordId -Value $Value -Name $Name
}

function Assert-LoopbackBackendUrl {
    param([Parameter(Mandatory = $true)][string]$Value)

    [Uri]$uri = $null
    if (-not [Uri]::TryCreate($Value.Trim(), [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttp -or
        -not $uri.IsLoopback) {
        throw 'BackendUrl must be an HTTP loopback URL.'
    }

    return $uri.AbsoluteUri.TrimEnd('/')
}

function Assert-BackendEndpointAvailable {
    param([Parameter(Mandatory = $true)][string]$Url)

    $uri = [Uri]$Url
    $client = New-Object System.Net.Sockets.TcpClient
    $occupied = $false
    try {
        $connect = $client.ConnectAsync($uri.Host, $uri.Port)
        $occupied = $connect.Wait(750) -and $client.Connected
    }
    catch {
        $occupied = $false
    }
    finally {
        $client.Dispose()
    }

    if (-not $occupied) {
        return
    }

    $listeners = @(Get-NetTCPConnection -LocalPort $uri.Port -State Listen `
        -ErrorAction SilentlyContinue)
    $ownerIds = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($ownerIds.Count -ne 1) {
        throw "BackendUrl $Url is owned by an unknown process. No process was stopped. " +
            'Close the port owner manually and run this helper again.'
    }

    $owner = Get-CimInstance Win32_Process `
        -Filter "ProcessId = $($ownerIds[0])" `
        -ErrorAction SilentlyContinue
    $commandLine = if ($null -eq $owner) { '' } else { [string]$owner.CommandLine }
    $match = [regex]::Match(
        $commandLine,
        '(?i)(?<state>[a-z]:\\[^"'']*\\LSOverlay-M9(?:4|5|6)-[0-9a-f]{32})\\backend-release\\LSOverlay\.Backend\.dll')
    if ($null -eq $owner -or $owner.Name -ne 'dotnet.exe' -or -not $match.Success) {
        throw "BackendUrl $Url is owned by an unknown process (PID $($ownerIds[0])). " +
            'No process was stopped. Close it manually and run this helper again.'
    }

    $previousState = [System.IO.Path]::GetFullPath($match.Groups['state'].Value)
    $temporaryRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $previousLeaf = [System.IO.Path]::GetFileName($previousState)
    if (-not $previousState.StartsWith(
            $temporaryRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $previousLeaf -notmatch '^LSOverlay-M9(?:4|5|6)-[0-9a-f]{32}$') {
        throw "BackendUrl $Url owner path failed the helper safety check. " +
            'No process was stopped.'
    }

    $previousShutdown = Join-Path $previousState 'shutdown.request'
    Write-Host "Previous helper-owned LSOverlay.Backend detected on $Url; requesting clean shutdown."
    [System.IO.File]::WriteAllText($previousShutdown, 'stop')
    try {
        $process = [System.Diagnostics.Process]::GetProcessById([int]$ownerIds[0])
        if (-not $process.WaitForExit(10000)) {
            throw "The previous helper-owned Backend did not exit cleanly. No forced stop was used. " +
                "Close PID $($ownerIds[0]) manually and run this helper again."
        }
    }
    catch [System.ArgumentException] {
        # The process exited between ownership inspection and the wait.
    }

    $probe = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $probe.ConnectAsync($uri.Host, $uri.Port)
        if ($connect.Wait(750) -and $probe.Connected) {
            throw "BackendUrl $Url is still in use after the previous helper Backend exited. " +
                'No additional process was stopped.'
        }
    }
    catch [System.AggregateException] {
        # Connection refusal confirms that the endpoint is free.
    }
    catch [System.Net.Sockets.SocketException] {
        # Connection refusal confirms that the endpoint is free.
    }
    finally {
        $probe.Dispose()
    }
}

function Invoke-CheckedDotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Clear-BackendEnvironment {
    foreach ($name in $backendEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}

function Clear-TokenMaterial {
    [Environment]::SetEnvironmentVariable($tokenName, $null, 'Process')
    $script:plainToken = $null
    if ($script:tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($script:tokenPointer)
        $script:tokenPointer = [IntPtr]::Zero
    }

    if ($null -ne $script:secureToken) {
        $script:secureToken.Dispose()
        $script:secureToken = $null
    }
}

function Wait-BackendHealth {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$Url)

    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if ($Process.HasExited) {
            throw "Backend exited before /healthz became ready. See $backendStderr"
        }

        try {
            $health = Invoke-RestMethod -Uri ($Url + '/healthz') -TimeoutSec 1
            if ($health.status -eq 'ok') {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    throw 'Backend /healthz did not become ready within 30 seconds.'
}

function Wait-DiscordPairingReady {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    for ($attempt = 0; $attempt -lt 240; $attempt++) {
        if ($Process.HasExited) {
            throw "Backend exited before Discord pairing became ready. See $backendStderr"
        }

        if ((Test-Path -LiteralPath $backendStdout) -and
            (Select-String -LiteralPath $backendStdout `
                -Pattern 'Discord pairing command: Available' `
                -Quiet)) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw 'Discord pairing command did not become ready within 60 seconds.'
}

function Start-WpfApplication {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not [string]::IsNullOrEmpty(
            [Environment]::GetEnvironmentVariable($tokenName, 'Process'))) {
        throw 'Bot Token isolation failed; WPF was not launched.'
    }

    return Start-Process -FilePath $ExecutablePath `
        -WorkingDirectory (Split-Path -Parent $ExecutablePath) `
        -PassThru
}

function Write-ValidationChecklist {
    param([Parameter(Mandatory = $true)][string]$Url)

    Write-Host ''
    Write-Host 'Remote Backend URL:'
    Write-Host $Url -ForegroundColor Cyan
    Write-Host ''
    Write-Host "$ValidationMilestone USER VALIDATION" -ForegroundColor Green
    Write-Host ''
    Write-Host '[ ] Settings -> Discord -> Main Chat Source = Remote'
    Write-Host "[ ] Backend URL = $Url"
    Write-Host '[ ] Start Pairing'
    Write-Host '[ ] Run /lsoverlay pair code:<code> in Discord'
    Write-Host '[ ] Remote state becomes Live'
    Write-Host '[ ] Authorized channel list appears'
    Write-Host ('[ ] Select #' + [char]0xBA54 + [char]0xC778)
    Write-Host '[ ] Recent 20 appear in real HUD'
    Write-Host '[ ] Send/edit/delete normal text'
    Write-Host '[ ] Send Sticker'
    Write-Host '[ ] Forward text'
    Write-Host '[ ] Forward image'
    Write-Host '[ ] Switch Remote -> Legacy and verify Chat'
    Write-Host '[ ] Verify Sales remains normal'
    if ($ValidationMilestone -in @('M9.5', 'M9.6', 'M9.7', 'M9.8', 'M9.8.1')) {
        Write-Host '[ ] Sales Tracking = ON and Remote Sales becomes Live'
        Write-Host ('[ ] Keep Discord on a non-sales channel and send a sale in #' +
            [char]0xD310 + [char]0xB9E4 + [char]0xBAA8 + [char]0xC9D1)
        Write-Host '[ ] Add SOLD, remove SOLD, add closed, remove closed'
        Write-Host '[ ] Confirm queue transitions exactly once with no duplicate alert'
        Write-Host '[ ] Turn Sales Tracking OFF and confirm Remote Sales becomes Disabled'
        Write-Host '[ ] Re-enable and confirm Remote Sales returns Live'
    }
    if ($ValidationMilestone -in @('M9.6', 'M9.7', 'M9.8', 'M9.8.1')) {
        Write-Host '[ ] Diagnostics shows Effective Sales Source = RemotePrimary'
        Write-Host '[ ] Keep Discord on any channel; Remote Sales remains Live'
        Write-Host '[ ] Restart Backend and verify RemoteRecovering with queue preserved'
        Write-Host '[ ] Verify RemotePrimary only after a fresh canonical bootstrap'
        Write-Host '[ ] Verify no duplicate queue entries or Personal Alert'
    }
    if ($ValidationMilestone -in @('M9.7', 'M9.8', 'M9.8.1')) {
        Write-Host '[ ] Unlock HUD and expand Queue Detail'
        Write-Host '[ ] Verify status controls appear only on your own Sales message'
        Write-Host '[ ] Set Selling, Negotiating, and Sold; verify one Bot marker at a time'
        Write-Host '[ ] Verify each status changes only after Discord read-back'
        Write-Host '[ ] Use Clear Bot status; verify all three Bot markers are removed'
        Write-Host '[ ] Add a status reaction manually in Discord and use Clear'
        Write-Host '[ ] Verify the manually added human reaction remains untouched'
        Write-Host '[ ] Verify another user reaction remains untouched'
        Write-Host '[ ] Lock HUD and verify controls are non-interactive/click-through'
        Write-Host '[ ] Disconnect/degrade Remote Sales and verify controls are disabled'
        Write-Host '[ ] Reconnect and verify controls restore only at RemotePrimary Live'
    }
    if ($ValidationMilestone -in @('M9.8', 'M9.8.1')) {
        Write-Host '[ ] Select Host 1 and verify only Host 1 drives Session HUD'
        Write-Host '[ ] Select Host 2 and verify only Host 2 drives Session HUD'
        Write-Host '[ ] Switch hosts without Backend restart or re-pair'
        Write-Host '[ ] Select an offline Host while the other is online: no automatic switch'
        Write-Host '[ ] Change selected Host player count and verify one event-driven update'
        Write-Host '[ ] Stop selected Host GTA Online and verify stale occupancy is cleared'
        Write-Host '[ ] Restart Backend and verify Reconnecting appears without fake Offline flicker'
        Write-Host '[ ] Enable Minimal HUD and verify only compact valid occupancy remains'
        Write-Host '[ ] Toggle Show GTA Session OFF/ON in HUD settings'
        Write-Host '[ ] Use Sales -> Test sound and verify the configured local volume'
        Write-Host '[ ] Become Next Seller and verify exactly one Next notification'
        Write-Host '[ ] Become Current Seller and verify exactly one Current notification'
        Write-Host '[ ] Relaunch/reconnect/source-handoff without a position change: no sound'
        Write-Host '[ ] Lock HUD and verify Session remains informational and click-through'
    }
    Write-Host '[ ] Switch back to Remote if desired'
    Write-Host '[ ] Close/relaunch WPF and verify no re-pair'
    Write-Host '[ ] Unpair/Forget and verify PairingRequired'
    Write-Host ''
    Write-Host 'For restart validation:' -ForegroundColor Yellow
    Write-Host '- Close the WPF app.'
    Write-Host '- Enter R in this helper to relaunch it.'
    Write-Host '- Keep this PowerShell helper running so Backend credentials remain valid.'
    Write-Host ''
}

function Stop-HelperWpfProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            $null = $Process.CloseMainWindow()
            if (-not $Process.WaitForExit(5000)) {
                Stop-Process -Id $Process.Id -Force
                $null = $Process.WaitForExit(5000)
            }
        }
    }
    catch {
        Write-Warning "The helper-owned WPF process could not be fully closed: $($_.Exception.Message)"
    }
}

function Stop-HelperBackendProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            [System.IO.File]::WriteAllText($shutdownFile, 'stop')
            if (-not $Process.WaitForExit(10000)) {
                Stop-Process -Id $Process.Id -Force
                $null = $Process.WaitForExit(5000)
            }
        }
    }
    catch {
        Write-Warning "The helper-owned Backend process could not be fully closed: $($_.Exception.Message)"
    }
}

function Remove-IsolatedStateDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $temporaryRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $leaf = [System.IO.Path]::GetFileName($resolved)
    if (-not $resolved.StartsWith(
            $temporaryRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($statePrefix, [System.StringComparison]::Ordinal)) {
        throw "Unsafe $ValidationMilestone state cleanup path rejected: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Preserve-BackendFailureLogs {
    if (-not (Test-Path -LiteralPath $backendStdout) -and
        -not (Test-Path -LiteralPath $backendStderr)) {
        return $null
    }

    New-Item -ItemType Directory -Path $failureLogDirectory -Force | Out-Null
    foreach ($source in @($backendStdout, $backendStderr)) {
        if (Test-Path -LiteralPath $source) {
            $destination = Join-Path $failureLogDirectory (
                [System.IO.Path]::GetFileName($source))
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }

    return $failureLogDirectory
}

try {
    Assert-RequiredPath -Path $solutionPath
    Assert-RequiredPath -Path $backendProject
    Assert-RequiredPath -Path $wpfProject
    Assert-RequiredPath -Path $publishProfile

    $BackendUrl = Assert-LoopbackBackendUrl -Value $BackendUrl
    Assert-BackendEndpointAvailable -Url $BackendUrl

    if ([string]::IsNullOrWhiteSpace($GuildId)) {
        $GuildId = Read-Host 'Target Discord Guild ID'
    }
    $GuildId = Normalize-DiscordId -Value $GuildId -Name 'Target Discord Guild ID'
    if ($ValidationMilestone -eq 'M9.8.1') {
        if (-not $PSBoundParameters.ContainsKey('SessionHost1Id')) {
            $SessionHost1Id = Read-Host 'GTA Session Host 1 Discord User ID [leave blank to disable Session HUD]'
        }
        if (-not $PSBoundParameters.ContainsKey('SessionHost2Id')) {
            $SessionHost2Id = Read-Host 'GTA Session Host 2 Discord User ID [optional]'
        }

        $SessionHost1Id = Normalize-OptionalDiscordId `
            -Value $SessionHost1Id `
            -Name 'GTA Session Host 1 Discord User ID'
        $SessionHost2Id = Normalize-OptionalDiscordId `
            -Value $SessionHost2Id `
            -Name 'GTA Session Host 2 Discord User ID'
        if ([string]::IsNullOrWhiteSpace($SessionHost1Id) -and
            -not [string]::IsNullOrWhiteSpace($SessionHost2Id)) {
            throw 'GTA Session Host 1 is required when Host 2 is configured.'
        }
        if (-not [string]::IsNullOrWhiteSpace($SessionHost1Id) -and
            $SessionHost1Id -eq $SessionHost2Id) {
            throw 'GTA Session Host 1 and Host 2 must use different Discord IDs.'
        }

        $TrackedHostIds = ''
    }
    else {
        if (-not $PSBoundParameters.ContainsKey('TrackedHostIds')) {
            $TrackedHostIds = Read-Host 'Tracked Host Discord User ID(s) [optional]'
        }

        $TrackedHostIds = Normalize-TrackedHostIds -Value $TrackedHostIds
        $SessionHost1Id = ''
        $SessionHost2Id = ''
    }
    $existingWpf = @(Get-Process -Name 'GachaOverlay.App' -ErrorAction SilentlyContinue)
    if ($existingWpf.Count -gt 0) {
        throw 'Close the currently running GachaOverlay application and run this helper again.'
    }

    $secureToken = Read-Host 'Discord Bot Token' -AsSecureString
    if ($secureToken.Length -eq 0) {
        throw 'Discord Bot Token cannot be empty.'
    }

    New-Item -ItemType Directory -Path $stateDirectory | Out-Null
    $stateDirectoryCreated = $true
    Write-Host 'Preparing isolated Release validation binaries...'
    Invoke-CheckedDotNet -Arguments @('restore', $solutionPath)
    Invoke-CheckedDotNet -Arguments @(
        'restore',
        $wpfProject,
        ('-p:PublishProfile=' + $publishProfile))
    Invoke-CheckedDotNet -Arguments @(
        'publish',
        $backendProject,
        '-c',
        'Release',
        '--no-restore',
        '-o',
        $backendOutput)
    $wpfPublishArguments = @(
        'publish',
        $wpfProject,
        '-c',
        'Release',
        '--no-restore',
        ('-p:PublishProfile=' + $publishProfile),
        '-o',
        $wpfOutput)
    Invoke-CheckedDotNet -Arguments $wpfPublishArguments

    $backendDll = Join-Path $backendOutput 'LSOverlay.Backend.dll'
    $wpfExecutable = Join-Path $wpfOutput 'GachaOverlay.App.exe'
    Assert-RequiredPath -Path $backendDll
    Assert-RequiredPath -Path $wpfExecutable

    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    [Environment]::SetEnvironmentVariable($tokenName, $plainToken, 'Process')
    [Environment]::SetEnvironmentVariable($guildName, $GuildId, 'Process')
    [Environment]::SetEnvironmentVariable($hostsName, $TrackedHostIds, 'Process')
    [Environment]::SetEnvironmentVariable($host1Name, $SessionHost1Id, 'Process')
    [Environment]::SetEnvironmentVariable($host2Name, $SessionHost2Id, 'Process')
    [Environment]::SetEnvironmentVariable($stateName, $stateDirectory, 'Process')
    [Environment]::SetEnvironmentVariable($listenName, $BackendUrl, 'Process')
    [Environment]::SetEnvironmentVariable($shutdownName, $shutdownFile, 'Process')

    $dotnetHost = (Get-Command dotnet -ErrorAction Stop).Source
    $backendArgument = '"' + $backendDll + '"'
    $backendProcess = Start-Process -FilePath $dotnetHost `
        -ArgumentList @($backendArgument) `
        -WorkingDirectory $backendOutput `
        -WindowStyle Hidden `
        -RedirectStandardOutput $backendStdout `
        -RedirectStandardError $backendStderr `
        -PassThru

    # Security boundary: no Backend environment variable, especially the Bot Token,
    # may be inherited by the real WPF validation process.
    Clear-BackendEnvironment
    Clear-TokenMaterial

    Wait-BackendHealth -Process $backendProcess -Url $BackendUrl
    Wait-DiscordPairingReady -Process $backendProcess
    Write-Host 'Backend: HTTP Ready'
    Write-Host 'Discord: Pairing Ready'
    Write-Host "Backend logs: $backendStdout"

    $checklistPrinted = $false
    $keepRunning = $true
    while ($keepRunning) {
        $activeWpfProcess = Start-WpfApplication -ExecutablePath $wpfExecutable
        if (-not $checklistPrinted) {
            Write-ValidationChecklist -Url $BackendUrl
            $checklistPrinted = $true
        }

        while (-not $activeWpfProcess.HasExited) {
            if ($backendProcess.HasExited) {
                throw "Backend exited during WPF validation. See $backendStderr"
            }

            Start-Sleep -Milliseconds 500
        }

        $activeWpfProcess = $null
        do {
            $choice = (Read-Host 'WPF exited. R = Relaunch WPF, Q = Quit validation').Trim().ToUpperInvariant()
        } while ($choice -notin @('R', 'Q'))

        if ($choice -eq 'Q') {
            $keepRunning = $false
        }
    }
}
catch {
    $failureMessage = $_.Exception.Message
    $preservedLogs = Preserve-BackendFailureLogs
    if (-not [string]::IsNullOrWhiteSpace($preservedLogs)) {
        throw "$failureMessage Backend logs preserved at $preservedLogs"
    }

    throw
}
finally {
    Clear-BackendEnvironment
    Clear-TokenMaterial
    Stop-HelperWpfProcess -Process $activeWpfProcess
    Stop-HelperBackendProcess -Process $backendProcess
    if ($stateDirectoryCreated) {
        Remove-IsolatedStateDirectory -Path $stateDirectory
    }

    Write-Host "$ValidationMilestone local validation session cleaned up."
}
