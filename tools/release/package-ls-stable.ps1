[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$QuickStartPath,
    [Parameter(Mandatory)][string]$GuidePath,
    [Parameter(Mandatory)][string]$FocusedTrxPath,
    [string]$ManifestPath = 'ls-2.1.1.json',
    [switch]$UserValidationCompleted,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedExeSha256
)

# Local Stable candidate assembly only. Never commits, pushes, tags, uploads, deploys,
# or changes Railway, DNS, OAuth or Discord configuration.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifestFile = if ([IO.Path]::IsPathRooted($ManifestPath)) {
    $ManifestPath
} else {
    Join-Path $PSScriptRoot $ManifestPath
}
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
$releasesRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/releases'))
if (-not $output.StartsWith($releasesRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be a new subdirectory of artifacts/releases.'
}
if (Test-Path -LiteralPath $output) {
    throw 'Stable candidate output already exists. Refusing to overwrite review artifacts.'
}

[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
if ($props.Project.PropertyGroup[0].GachaReleaseVersion -ne $version) {
    throw 'Stable version metadata does not match the manifest.'
}

$publish = (Resolve-Path -LiteralPath $PublishRoot).Path
$quickStart = (Resolve-Path -LiteralPath $QuickStartPath).Path
$guide = (Resolve-Path -LiteralPath $GuidePath).Path
$exeSource = Join-Path $publish 'GachaOverlay.App.exe'
$allowedPublishFiles = @(
    'GachaOverlay.App.exe',
    'Assets/Media/ThirdPartyNotices/SkiaSharp.txt'
)
$unexpectedPublishFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object {
    $relative = $_.FullName.Substring($publish.Length + 1).Replace('\', '/')
    $relative -notin $allowedPublishFiles
})
if (-not (Test-Path -LiteralPath $exeSource -PathType Leaf) -or $unexpectedPublishFiles.Count -gt 0) {
    $names = $unexpectedPublishFiles.FullName -join ', '
    throw "Publish root contains an unexpected file or lacks GachaOverlay.App.exe: $names"
}
if ((Get-FileHash -LiteralPath $exeSource -Algorithm SHA256).Hash -ne $ExpectedExeSha256) {
    throw 'Executable SHA-256 differs from the approved candidate.'
}
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($exeSource)
if ($versionInfo.ProductVersion -ne $version -or
    $versionInfo.FileVersion -ne [string]$manifest.fileVersion -or
    $versionInfo.ProductName -ne 'LS Overlay' -or
    $versionInfo.FileDescription -ne 'LS Overlay') {
    throw 'Executable metadata mismatch.'
}

[xml]$trx = Get-Content -LiteralPath (Resolve-Path -LiteralPath $FocusedTrxPath).Path -Raw
$counters = $trx.TestRun.ResultSummary.Counters
if ([int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or
    [int]$counters.passed -ne [int]$counters.total -or [int]$counters.total -lt 1) {
    throw 'Focused release validation evidence is not a PASS.'
}

$licenses = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'license-manifest.json') -Raw | ConvertFrom-Json
foreach ($license in $licenses.entries) {
    if ($license.blocksFinalPackage -or -not ([string]$license.status).StartsWith('Verified')) {
        throw "License verification missing: $($license.component)"
    }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("LSOverlay-$version-Stable-" + [guid]::NewGuid().ToString('N'))
$package = Join-Path $staging 'package'
$fresh = Join-Path $staging 'fresh-extraction'
New-Item -ItemType Directory -Path $package | Out-Null

$sources = [ordered]@{
    'LSOverlay.exe' = $exeSource
    'README.md' = (Join-Path $PSScriptRoot "README-public-$version.md")
    'LICENSE' = (Join-Path $repo 'LICENSE')
    ([string]$manifest.quickStartName) = $quickStart
    ([string]$manifest.guideName) = $guide
    'Licenses/THIRD-PARTY-NOTICES.txt' = (Join-Path $PSScriptRoot 'licenses/THIRD-PARTY-NOTICES.txt')
}
foreach ($license in $licenses.entries) {
    $root = (Resolve-Path -LiteralPath (Join-Path $repo ([string]$license.sourcePath))).Path
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $sources["Licenses/$($license.stagingDirectory)/$relative"] = $file.FullName
    }
}

$forbiddenText = '(?i)(?:C:\\Users\\|E:\\Codex\\|(?:client_secret|access_token|bot_token)\s*[:=]\s*[a-z0-9._-]{16,})'
foreach ($name in $sources.Keys) {
    $destination = Join-Path $package $name
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $sources[$name] -Destination $destination
    if ([IO.Path]::GetExtension($destination) -in '.txt', '.md' -and
        (Get-Content -LiteralPath $destination -Raw) -match $forbiddenText) {
        throw "Unexpected private content in public file: $name"
    }
}

# Run only the extracted EXE, through the existing single-instance early-exit path.
# This validates the app host and bundled runtime without profile/network mutation.
function Test-ExtractedStartup([string]$Executable, [string]$WorkingDirectory) {
$created = $false
$mutex = [Threading.Mutex]::new($true,
    'Local\GachaOverlay.Foundation.74B75E39-1972-4FA1-B718-5546F7D85E30', [ref]$created)
try {
    $process = Start-Process -FilePath $Executable -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        throw 'Isolated executable smoke timed out.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Isolated executable smoke failed: $($process.ExitCode)."
    }
} finally {
    if ($created) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
}

New-Item -ItemType Directory -Path $output | Out-Null
foreach ($entry in Get-ChildItem -LiteralPath $package) {
    Copy-Item -LiteralPath $entry.FullName -Destination $output -Recurse
}

$zipPath = Join-Path $output ([string]$manifest.zipName)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $archiveTimestamp = [DateTimeOffset]::Parse([string]$manifest.archiveTimestampUtc)
    foreach ($name in @($sources.Keys | Sort-Object)) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $archiveTimestamp
        $inputStream = [IO.File]::OpenRead((Join-Path $package $name))
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
} finally {
    $zip.Dispose()
}

New-Item -ItemType Directory -Path $fresh | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $fresh)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($archive.Entries.FullName | Sort-Object)
    if (Compare-Object @($sources.Keys | Sort-Object) $actual) {
        throw 'ZIP allowlist mismatch.'
    }
    foreach ($name in $actual) {
        if ((Get-FileHash -LiteralPath (Join-Path $package $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $fresh $name)).Hash) {
            throw "Fresh extraction hash mismatch: $name"
        }
    }
} finally {
    $archive.Dispose()
}

Test-ExtractedStartup -Executable (Join-Path $fresh 'LSOverlay.exe') -WorkingDirectory $fresh
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $fresh 'LSOverlay.exe')).ProductVersion -ne $version) {
    throw 'Extracted executable version mismatch.'
}
foreach ($relativeLink in @([string]$manifest.quickStartName, [string]$manifest.guideName, 'LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $fresh $relativeLink) -PathType Leaf)) {
        throw "Broken package README link: $relativeLink"
    }
}

$artifactNames = @(
    'LSOverlay.exe',
    [string]$manifest.zipName,
    [string]$manifest.quickStartName,
    [string]$manifest.guideName
)
$artifactInfo = @()
$hashLines = @()
foreach ($name in $artifactNames) {
    $file = Get-Item -LiteralPath (Join-Path $output $name)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    $artifactInfo += [ordered]@{ filename = $name; bytes = $file.Length; sha256 = $hash }
    $hashLines += "$hash  $name"
}
$checksumPath = Join-Path $output ([string]$manifest.checksumName)
[IO.File]::WriteAllLines($checksumPath, $hashLines, [Text.UTF8Encoding]::new($false))

$publicManifest = [ordered]@{
    product = 'LS Overlay'
    version = $version
    configuration = 'Release'
    rid = 'win-x64'
    selfContained = $true
    singleFile = $true
    sourceBaseCommit = (& git -C $repo rev-parse HEAD).Trim()
    includesUncommittedStablePreparation = @(& git -C $repo status --porcelain).Count -gt 0
    publication = 'NOT PUBLISHED'
    codeSigning = 'NotSigned'
    tests = [ordered]@{
        total = [int]$counters.total
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted
    }
    artifacts = $artifactInfo
    archiveContents = $actual
    isolatedLaunch = 'PASS - secondary-instance path; no profile/network/interactive UI'
    interactiveUserReview = if ($UserValidationCompleted) { 'PASS - user reported actual-PC corrective validation' } else { 'PENDING' }
}
[IO.File]::WriteAllText(
    (Join-Path $output "LS-Overlay-$version-manifest.json"),
    ($publicManifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false)
)

foreach ($artifact in $artifactInfo) {
    if ((Get-FileHash -LiteralPath (Join-Path $output $artifact.filename)).Hash -ne $artifact.sha256) {
        throw "Final checksum mismatch: $($artifact.filename)"
    }
}

Write-Output ($publicManifest | ConvertTo-Json -Depth 8)
Write-Output "Local Stable review set: $output"
