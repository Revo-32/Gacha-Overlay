[CmdletBinding()]
param(
    [switch]$Finalize
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot '..\..'))
$versionPropsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$manifestPath = Join-Path $scriptRoot 'release-manifest.json'
$licenseManifestPath = Join-Path $scriptRoot 'license-manifest.json'
$verificationScript = Join-Path $scriptRoot 'verify-release.ps1'
$solutionPath = Join-Path $repositoryRoot 'GachaOverlay.sln'
$appProjectPath = Join-Path $repositoryRoot 'src\GachaOverlay.App\GachaOverlay.App.csproj'
$publishProfilePath = Join-Path $repositoryRoot 'src\GachaOverlay.App\Properties\PublishProfiles\win-x64-singlefile.pubxml'

function Get-ReleaseVersion {
    [xml]$props = Get-Content -LiteralPath $versionPropsPath -Raw
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/GachaReleaseVersion')
    $value = if ($null -eq $versionNode) { '' } else { [string]$versionNode.InnerText }
    if ($value -notmatch '^\d+\.\d+\.\d+-rc\.\d+$') {
        throw "Directory.Build.props contains an invalid GachaReleaseVersion: '$value'."
    }

    return $value
}

function Assert-PathWithinReleaseRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ReleaseRoot
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($ReleaseRoot)
    $rootPrefix = $resolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release path rejected: '$resolvedPath'."
    }
}

function Remove-BoundedDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ReleaseRoot
    )

    Assert-PathWithinReleaseRoot -Path $Path -ReleaseRoot $ReleaseRoot
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-GachaOverlayStopped {
    $running = @()
    foreach ($processName in @('GachaOverlay.App', 'Gacha Overlay')) {
        $running += @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    }

    if ($running.Count -gt 0) {
        throw 'Release build를 시작할 수 없습니다. Gacha Overlay를 종료한 뒤 다시 실행하세요.'
    }
}

function Copy-LicenseDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required license source directory is missing: '$Source'."
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

$version = Get-ReleaseVersion
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.version -ne $version) {
    throw "Version mismatch: Directory.Build.props='$version', release-manifest.json='$($manifest.version)'."
}

if (-not $Finalize) {
    Write-Host 'M8.3.1 prepare-only mode. No build, test, publish, package, or process interaction was performed.'
    Write-Host "Version: $version"
    Write-Host "Final command after soak/manual completion: .\tools\release\build-release.ps1 -Finalize"
    return
}

Assert-GachaOverlayStopped

$licenseManifest = Get-Content -LiteralPath $licenseManifestPath -Raw | ConvertFrom-Json
$licenseBlockers = @($licenseManifest.entries | Where-Object {
        $_.blocksFinalPackage -eq $true -and $_.status -ne 'Verified'
    })
if ($licenseBlockers.Count -gt 0) {
    $names = $licenseBlockers.component -join ', '
    throw "Final license verification is incomplete: $names."
}

$manualSource = Join-Path $repositoryRoot ([string]$manifest.manualSource)
if (-not (Test-Path -LiteralPath $manualSource -PathType Leaf)) {
    throw "Final Manual PDF is required: '$manualSource'. Complete M8.3.2 first."
}

$quickStartSource = Join-Path $repositoryRoot ([string]$manifest.quickStartSource)
if (-not (Test-Path -LiteralPath $quickStartSource -PathType Leaf)) {
    throw "Final Quick Start PDF is required: '$quickStartSource'. Complete M8.3.3 documentation first."
}

$artifactsRoot = Join-Path $repositoryRoot 'artifacts\release'
$releaseRoot = Join-Path $artifactsRoot $version
$stagingRoot = Join-Path $releaseRoot 'staging'
$publishRoot = Join-Path $stagingRoot 'publish'
$freshRoot = Join-Path $stagingRoot 'fresh-extraction'
$packageContainer = Join-Path $releaseRoot 'package'
$packageRoot = Join-Path $packageContainer ([string]$manifest.packageDirectoryName)
$metadataRoot = Join-Path $releaseRoot 'metadata'
$zipPath = Join-Path $releaseRoot ([string]$manifest.artifactName)

$artifactsPrefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not ([System.IO.Path]::GetFullPath($releaseRoot)).StartsWith(
        $artifactsPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe release root rejected: '$releaseRoot'."
}

foreach ($boundedDirectory in @($stagingRoot, $packageContainer, $metadataRoot)) {
    Remove-BoundedDirectory -Path $boundedDirectory -ReleaseRoot $releaseRoot
}

if (Test-Path -LiteralPath $zipPath) {
    Assert-PathWithinReleaseRoot -Path $zipPath -ReleaseRoot $releaseRoot
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $metadataRoot -Force | Out-Null

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', $solutionPath)
    Invoke-DotNet -Arguments @('build', $solutionPath)
    Invoke-DotNet -Arguments @('build', $solutionPath, '-c', 'Release')
    Invoke-DotNet -Arguments @('test', $solutionPath)
    Invoke-DotNet -Arguments @('test', $solutionPath, '-c', 'Release')
    Invoke-DotNet -Arguments @('format', $solutionPath, '--verify-no-changes')
    Invoke-DotNet -Arguments @(
        'restore',
        $appProjectPath,
        ('-p:PublishProfile=' + $publishProfilePath))
    Invoke-DotNet -Arguments @(
        'publish',
        $appProjectPath,
        '-c',
        'Release',
        '--no-restore',
        ('-p:PublishProfile=' + $publishProfilePath),
        '-o',
        $publishRoot)
}
finally {
    Pop-Location
}

$publishedExecutable = Join-Path $publishRoot 'GachaOverlay.App.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Published executable was not found: '$publishedExecutable'."
}

$finalExecutable = Join-Path $packageRoot ([string]$manifest.executableName)
Copy-Item -LiteralPath $publishedExecutable -Destination $finalExecutable
Copy-Item -LiteralPath $quickStartSource -Destination (Join-Path $packageRoot ([string]$manifest.quickStartName))
Copy-Item -LiteralPath $manualSource -Destination (Join-Path $packageRoot ([string]$manifest.manualName))

$licensesRoot = Join-Path $packageRoot 'Licenses'
New-Item -ItemType Directory -Path $licensesRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $scriptRoot 'licenses\THIRD-PARTY-NOTICES.txt') -Destination $licensesRoot

foreach ($verifiedLicense in @($licenseManifest.entries | Where-Object {
            ([string]$_.status).StartsWith(
                'Verified',
                [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::IsNullOrWhiteSpace([string]$_.sourcePath)
        })) {
    $source = Join-Path $repositoryRoot ([string]$verifiedLicense.sourcePath)
    $destination = Join-Path $licensesRoot ([string]$verifiedLicense.stagingDirectory)
    Copy-LicenseDirectory -Source $source -Destination $destination
}

$verificationReport = Join-Path $metadataRoot 'verification-report.md'
& $verificationScript `
    -PackageRoot $packageRoot `
    -ExpectedVersion $version `
    -ReportPath $verificationReport
if ($LASTEXITCODE -ne 0) {
    throw "Package verification failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $metadataRoot 'release-manifest.json')
Copy-Item -LiteralPath $licenseManifestPath -Destination (Join-Path $metadataRoot 'license-manifest.json')

$exeHash = (Get-FileHash -LiteralPath $finalExecutable -Algorithm SHA256).Hash
$checksumPath = Join-Path $metadataRoot 'SHA256SUMS.txt'
Set-Content -LiteralPath $checksumPath -Encoding utf8 -Value (
    "$exeHash  $($manifest.executableName)")

Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
New-Item -ItemType Directory -Path $freshRoot -Force | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $freshRoot
$freshPackageRoot = Join-Path $freshRoot ([string]$manifest.packageDirectoryName)
& $verificationScript -PackageRoot $freshPackageRoot -ExpectedVersion $version
if ($LASTEXITCODE -ne 0) {
    throw "Fresh extraction verification failed with exit code $LASTEXITCODE."
}

& $verificationScript -ZipPath $zipPath -ExpectedVersion $version
if ($LASTEXITCODE -ne 0) {
    throw "ZIP structural verification failed with exit code $LASTEXITCODE."
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Add-Content -LiteralPath $checksumPath -Encoding utf8 -Value (
    "$zipHash  $($manifest.artifactName)")

Write-Host "Release Candidate assembled: $zipPath"
Write-Host "ZIP SHA-256: $zipHash"
