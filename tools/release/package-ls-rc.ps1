[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$GuidePath,
    [Parameter(Mandatory)][string]$DebugTrxPath,
    [Parameter(Mandatory)][string]$ReleaseTrxPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedExeSha256
)

# Local assembly only. Never builds, publishes remotely, alters profiles, or overwrites an RC set.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifest = Get-Content (Join-Path $PSScriptRoot 'ls-2.0.0-rc.1.json') -Raw | ConvertFrom-Json
$version = $manifest.version
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
$releasesRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/releases'))
if (-not $output.StartsWith($releasesRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be a new subdirectory of artifacts/releases.'
}
if (Test-Path -LiteralPath $output) { throw 'RC output already exists. Refusing to overwrite immutable artifacts.' }
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
if ($props.Project.PropertyGroup[0].GachaReleaseVersion -ne $version) { throw 'RC version mismatch.' }
$publish = (Resolve-Path -LiteralPath $PublishRoot).Path
$guide = (Resolve-Path -LiteralPath $GuidePath).Path
$exeSource = Join-Path $publish 'GachaOverlay.App.exe'
$files = @(Get-ChildItem -LiteralPath $publish -Force)
if ($files.Count -ne 1 -or $files[0].Name -ne 'GachaOverlay.App.exe') { throw 'Publish must contain the single executable only.' }
if ((Get-FileHash -LiteralPath $exeSource -Algorithm SHA256).Hash -ne $ExpectedExeSha256) {
    throw 'Executable SHA-256 differs from the approved candidate. Refusing to package another build.'
}
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exeSource)
if ($info.ProductVersion -ne $version -or $info.FileVersion -ne '2.0.0.0' -or
    $info.ProductName -ne 'LS Overlay' -or $info.FileDescription -ne 'LS Overlay') { throw 'Executable metadata mismatch.' }

$results = @{}
$testEvidence = @{}
$trxPaths = @{ debug = $DebugTrxPath; release = $ReleaseTrxPath }
foreach ($configuration in @('debug', 'release')) {
    $trxPath = (Resolve-Path -LiteralPath $trxPaths[$configuration]).Path
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or
        [int]$counters.passed -ne [int]$counters.total -or [int]$counters.total -lt 1534) {
        throw "The final $configuration test result is not a full PASS."
    }
    $results[$configuration] = @{ total = [int]$counters.total; passed = [int]$counters.passed; failed = 0; skipped = 0 }
    $testEvidence[$configuration] = @{
        filename = [IO.Path]::GetFileName($trxPath)
        sha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash
        finishedUtc = ([DateTimeOffset]::Parse([string]$trx.TestRun.Times.finish)).UtcDateTime.ToString('o')
    }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ('LSOverlay-RC-Package-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $staging 'package'
New-Item -ItemType Directory -Path $package | Out-Null
$sources = [ordered]@{
    'LSOverlay.exe' = $exeSource
    'README.md' = (Join-Path $repo 'docs/user/QUICK-START-ko.md')
    'LICENSE' = (Join-Path $repo 'LICENSE')
}
$sources[$manifest.guideName] = $guide
$sources['Licenses/THIRD-PARTY-NOTICES.txt'] = Join-Path $PSScriptRoot 'licenses/THIRD-PARTY-NOTICES.txt'
$licenses = Get-Content (Join-Path $PSScriptRoot 'license-manifest.json') -Raw | ConvertFrom-Json
foreach ($license in $licenses.entries) {
    if ($license.blocksFinalPackage -or -not $license.status.StartsWith('Verified')) { throw 'License verification missing.' }
    $root = (Resolve-Path (Join-Path $repo $license.sourcePath)).Path
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $sources["Licenses/$($license.stagingDirectory)/$relative"] = $file.FullName
    }
}
foreach ($name in $sources.Keys) {
    $destination = Join-Path $package $name
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $sources[$name] -Destination $destination
}
$forbiddenText = '(?i)(?:C:\\Users\\|E:\\Codex\\|(?:client_secret|access_token|bot_token)\s*[:=]\s*[a-z0-9._-]{16,})'
foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File) {
    if ($file.Extension -in '.txt', '.md' -and (Get-Content -LiteralPath $file.FullName -Raw) -match $forbiddenText) {
        throw "Unexpected private content in public file: $($file.Name)"
    }
}

# One fresh-directory launch, no UI automation: force the existing secondary-instance
# early exit before ApplicationHost/profile/network startup. App.xaml resources and
# the bundled runtime must load successfully. Full interactive startup remains a user check.
$isolated = Join-Path $staging 'isolated'
New-Item -ItemType Directory -Path $isolated | Out-Null
$isolatedExe = Join-Path $isolated 'LSOverlay.exe'
Copy-Item -LiteralPath (Join-Path $package 'LSOverlay.exe') -Destination $isolatedExe
$created = $false
$mutex = [Threading.Mutex]::new($true, 'Local\GachaOverlay.Foundation.74B75E39-1972-4FA1-B718-5546F7D85E30', [ref]$created)
try {
    $process = Start-Process -FilePath $isolatedExe -WorkingDirectory $isolated -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        throw 'Isolated package startup timed out (only helper-owned process stopped).'
    }
    if ($process.ExitCode -ne 0) { throw "Isolated package startup failed: $($process.ExitCode)." }
} finally {
    if ($created) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

New-Item -ItemType Directory -Path $output | Out-Null
foreach ($entry in Get-ChildItem -LiteralPath $package) {
    Copy-Item -LiteralPath $entry.FullName -Destination $output -Recurse
}
$zipPath = Join-Path $output $manifest.zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @($sources.Keys | Sort-Object)) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026, 9, 3, 0, 0, 0, [TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead((Join-Path $package $name))
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
} finally { $zip.Dispose() }
$fresh = Join-Path $staging 'fresh-extraction'
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $fresh)
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($zip.Entries.FullName | Sort-Object)
    if (Compare-Object @($sources.Keys | Sort-Object) $actual) { throw 'ZIP allowlist mismatch.' }
    foreach ($name in $actual) {
        if ((Get-FileHash -LiteralPath (Join-Path $package $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $fresh $name)).Hash) { throw "ZIP hash mismatch: $name" }
    }
} finally { $zip.Dispose() }

# Final ZIP is closed and validated before checksumming. Never mutate it below.
$hashes = @()
$artifactInfo = @()
foreach ($name in @($manifest.zipName, 'LSOverlay.exe', $manifest.guideName)) {
    $file = Get-Item -LiteralPath (Join-Path $output $name)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $hashes += "$hash  $name"
    $artifactInfo += @{ filename = $name; bytes = $file.Length; sha256 = $hash }
}
$checksumPath = Join-Path $output "LS-Overlay-$version-SHA256.txt"
[IO.File]::WriteAllLines($checksumPath, $hashes, [Text.UTF8Encoding]::new($false))
$head = (& git -C $repo rev-parse HEAD).Trim()
$dirty = @(& git -C $repo status --porcelain).Count -gt 0
$publicManifest = [ordered]@{
    product = 'LS Overlay'; version = $version; configuration = 'Release'; rid = 'win-x64'
    selfContained = $true; singleFile = $true; assemblyName = 'GachaOverlay.App'
    sourceBaseCommit = $head; includesUncommittedRcChanges = $dirty; assembledUtc = [DateTime]::UtcNow.ToString('o')
    codeSigning = 'NotSigned'; artifacts = $artifactInfo; archiveContents = $actual; tests = $results
    approvedCandidateSha256 = $ExpectedExeSha256.ToUpperInvariant(); testEvidence = $testEvidence
    isolatedLaunch = 'PASS - secondary-instance path; no profile/network/interactive UI'
    fullInteractiveLaunch = 'USER VALIDATION PENDING'; publication = 'NOT PUBLISHED'
}
[IO.File]::WriteAllText((Join-Path $output 'LS-Overlay-2.0.0-rc.1-manifest.json'),
    ($publicManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
foreach ($artifact in $artifactInfo) {
    if ((Get-FileHash -LiteralPath (Join-Path $output $artifact.filename)).Hash -ne $artifact.sha256) { throw 'Final checksum mismatch.' }
}
Write-Output ($publicManifest | ConvertTo-Json -Depth 8)
Write-Output "Local artifact set: $output"
