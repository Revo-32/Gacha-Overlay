[CmdletBinding()]
param(
    [string]$ClientPath = (Join-Path $PSScriptRoot '..\..\src\GachaOverlay.App\bin\Release\net8.0-windows\GachaOverlay.App.exe')
)

$ErrorActionPreference = 'Stop'

$stagingEndpoint = 'https://lsoverlaybackend-staging.up.railway.app'
$clientPath = [System.IO.Path]::GetFullPath($ClientPath)
$dataDirectory = [System.IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GachaOverlay'))
$settingsPath = Join-Path $dataDirectory 'settings.json'
$settingsBackupPath = Join-Path $dataDirectory 'settings.json.bak'
$credentialPath = Join-Path $dataDirectory 'remote-access-token.dat'

function Get-FileSnapshot {
    param([Parameter(Mandatory)][string]$LiteralPath)

    if (Test-Path -LiteralPath $LiteralPath -PathType Leaf) {
        return [pscustomobject]@{
            Exists = $true
            Bytes = [System.IO.File]::ReadAllBytes($LiteralPath)
        }
    }

    return [pscustomobject]@{
        Exists = $false
        Bytes = $null
    }
}

function Restore-FileSnapshot {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)]$Snapshot
    )

    if (-not $Snapshot.Exists) {
        if (Test-Path -LiteralPath $LiteralPath -PathType Leaf) {
            Remove-Item -LiteralPath $LiteralPath -Force
        }
        return
    }

    $temporaryPath = "$LiteralPath.staging-launch.$PID.tmp"
    try {
        [System.IO.File]::WriteAllBytes($temporaryPath, $Snapshot.Bytes)
        [System.IO.File]::Copy($temporaryPath, $LiteralPath, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
    throw "Release client not found: $clientPath"
}

if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "Settings file not found. Run the normal client once before using this helper: $settingsPath"
}

$runningOverlay = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('GachaOverlay.App', 'LSOverlay')
}
if ($runningOverlay) {
    throw 'LS Overlay is already running. Exit it from the tray, then run this helper again.'
}

$settingsSnapshot = Get-FileSnapshot -LiteralPath $settingsPath
$settingsBackupSnapshot = Get-FileSnapshot -LiteralPath $settingsBackupPath
$credentialSnapshot = Get-FileSnapshot -LiteralPath $credentialPath
$temporarySettingsPath = "$settingsPath.staging-launch.$PID.tmp"

try {
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $endpointProperty = $settings.PSObject.Properties['remoteBackendBaseUrl']
    if ($null -eq $endpointProperty) {
        throw "Existing development endpoint setting was not found in: $settingsPath"
    }

    $endpointProperty.Value = $stagingEndpoint
    $settingsJson = $settings | ConvertTo-Json -Depth 100
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporarySettingsPath, $settingsJson + [Environment]::NewLine, $utf8WithoutBom)
    [System.IO.File]::Copy($temporarySettingsPath, $settingsPath, $true)
    Remove-Item -LiteralPath $temporarySettingsPath -Force

    # The protected Remote credential is endpoint-specific. Hide the production
    # credential for this process lifetime so staging can complete its own OAuth.
    if (Test-Path -LiteralPath $credentialPath -PathType Leaf) {
        Remove-Item -LiteralPath $credentialPath -Force
    }

    Write-Host "Starting LS Overlay against staging: $stagingEndpoint"
    Write-Host 'Keep this PowerShell window open. Exit LS Overlay from the tray when validation is complete.'

    $client = Start-Process -FilePath $clientPath -WorkingDirectory (Split-Path -Parent $clientPath) -PassThru
    $client.WaitForExit()
}
finally {
    if (Test-Path -LiteralPath $temporarySettingsPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporarySettingsPath -Force
    }

    $restoreFailure = $null
    try {
        Restore-FileSnapshot -LiteralPath $settingsPath -Snapshot $settingsSnapshot
    }
    catch {
        $restoreFailure = $_
    }
    try {
        Restore-FileSnapshot -LiteralPath $settingsBackupPath -Snapshot $settingsBackupSnapshot
    }
    catch {
        if ($null -eq $restoreFailure) { $restoreFailure = $_ }
    }
    try {
        Restore-FileSnapshot -LiteralPath $credentialPath -Snapshot $credentialSnapshot
    }
    catch {
        if ($null -eq $restoreFailure) { $restoreFailure = $_ }
    }

    if ($null -ne $restoreFailure) {
        throw "One or more production files could not be restored: $($restoreFailure.Exception.Message)"
    }
    Write-Host 'Production endpoint, settings, and protected Remote credential restored.'
}
