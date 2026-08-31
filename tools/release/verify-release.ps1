[CmdletBinding(DefaultParameterSetName = 'Package')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Package')]
    [string]$PackageRoot,

    [Parameter(Mandatory, ParameterSetName = 'Zip')]
    [string]$ZipPath,

    [string]$ExpectedVersion,

    [Parameter(ParameterSetName = 'Package')]
    [switch]$AllowMissingManual,

    [Parameter(ParameterSetName = 'Package')]
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$manifestPath = Join-Path $scriptRoot 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = [string]$manifest.version
}

if ([string]$manifest.version -ne $ExpectedVersion) {
    throw "Expected version '$ExpectedVersion' does not match release manifest '$($manifest.version)'."
}

function Assert-ReleaseCondition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-ForbiddenRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $leaf = [System.IO.Path]::GetFileName($RelativePath)
    foreach ($forbiddenName in @($manifest.forbiddenRecursiveFileNames)) {
        if ($leaf.Equals(
                [string]$forbiddenName,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    foreach ($forbiddenExtension in @($manifest.forbiddenRecursiveExtensions)) {
        if ($RelativePath.EndsWith(
                [string]$forbiddenExtension,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Assert-AllowedRootEntries {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileSystemInfo[]]$Entries
    )

    $allowedNames = @($manifest.allowedRootEntries | ForEach-Object { [string]$_ })
    foreach ($entry in $Entries) {
        Assert-ReleaseCondition `
            -Condition ($allowedNames -contains $entry.Name) `
            -Message "Unexpected package-root entry: '$($entry.Name)'."
    }

    foreach ($required in @($manifest.requiredRootEntries)) {
        if ($AllowMissingManual -and [string]$required.name -eq [string]$manifest.manualName) {
            continue
        }

        $match = @($Entries | Where-Object { $_.Name -eq [string]$required.name })
        Assert-ReleaseCondition `
            -Condition ($match.Count -eq 1) `
            -Message "Required package-root entry is missing or duplicated: '$($required.name)'."

        $expectedContainer = [string]$required.type -eq 'directory'
        Assert-ReleaseCondition `
            -Condition ($match[0].PSIsContainer -eq $expectedContainer) `
            -Message "Required entry has the wrong type: '$($required.name)'."
    }
}

function Assert-NoSecretLikeContent {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $patterns = @(
        '(?i)discord[-_]?oauth[-_]?token\s*[:=]\s*[A-Za-z0-9._-]{16,}',
        '(?i)client[-_]?secret\s*[:=]\s*[A-Za-z0-9._-]{16,}',
        '(?i)C:\\Users\\[^\\\r\n]+\\',
        '(?i)E:\\Codex\\Projects\\'
    )
    $textExtensions = @('.txt', '.md', '.json', '.xml', '.config', '.ini', '.log')

    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File)) {
        if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) {
            continue
        }

        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($pattern in $patterns) {
            Assert-ReleaseCondition `
                -Condition ($content -notmatch $pattern) `
                -Message "Secret-like or developer-local content was found in '$($file.Name)'."
        }
    }
}

function Test-ZipEntryForbidden {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.EndsWith('/')) {
        return $false
    }

    return Test-ForbiddenRelativePath -RelativePath $RelativePath
}

if ($PSCmdlet.ParameterSetName -eq 'Zip') {
    $resolvedZip = [System.IO.Path]::GetFullPath($ZipPath)
    Assert-ReleaseCondition `
        -Condition (Test-Path -LiteralPath $resolvedZip -PathType Leaf) `
        -Message "ZIP was not found: '$resolvedZip'."

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedZip)
    try {
        $rootPrefix = ([string]$manifest.packageDirectoryName).TrimEnd('/') + '/'
        $fileEntries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        Assert-ReleaseCondition `
            -Condition ($fileEntries.Count -gt 0) `
            -Message 'ZIP has no file entries.'

        foreach ($entry in $fileEntries) {
            Assert-ReleaseCondition `
                -Condition ($entry.FullName.StartsWith(
                        $rootPrefix,
                        [System.StringComparison]::Ordinal)) `
                -Message "ZIP entry is outside the expected root: '$($entry.FullName)'."

            $relativePath = $entry.FullName.Substring($rootPrefix.Length)
            $relativeSegments = @($relativePath -split '/')
            Assert-ReleaseCondition `
                -Condition ($relativeSegments -notcontains '..') `
                -Message "ZIP entry contains a parent traversal segment: '$relativePath'."

            $allowedRootNames = @($manifest.allowedRootEntries | ForEach-Object { [string]$_ })
            Assert-ReleaseCondition `
                -Condition ($allowedRootNames -contains $relativeSegments[0]) `
                -Message "Unexpected ZIP package-root entry: '$($relativeSegments[0])'."

            Assert-ReleaseCondition `
                -Condition (-not (Test-ZipEntryForbidden -RelativePath $relativePath)) `
                -Message "Forbidden ZIP entry: '$relativePath'."
        }

        foreach ($required in @($manifest.requiredRootEntries)) {
            $requiredPath = $rootPrefix + [string]$required.name
            if ([string]$required.type -eq 'directory') {
                $requiredPath += '/'
                $found = @($archive.Entries | Where-Object {
                        $_.FullName.StartsWith(
                            $requiredPath,
                            [System.StringComparison]::Ordinal)
                    }).Count -gt 0
            }
            else {
                $found = @($archive.Entries | Where-Object {
                        $_.FullName.Equals(
                            $requiredPath,
                            [System.StringComparison]::Ordinal)
                    }).Count -eq 1
            }

            Assert-ReleaseCondition `
                -Condition $found `
                -Message "Required ZIP entry is missing: '$($required.name)'."
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "PASS: ZIP structure matches $ExpectedVersion manifest."
    return
}

$resolvedPackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
Assert-ReleaseCondition `
    -Condition (Test-Path -LiteralPath $resolvedPackageRoot -PathType Container) `
    -Message "Package root was not found: '$resolvedPackageRoot'."

$rootEntries = @(Get-ChildItem -LiteralPath $resolvedPackageRoot -Force)
Assert-AllowedRootEntries -Entries $rootEntries

foreach ($rootFile in @($rootEntries | Where-Object { -not $_.PSIsContainer })) {
    foreach ($forbiddenExtension in @($manifest.forbiddenRootExtensions)) {
        Assert-ReleaseCondition `
            -Condition (-not $rootFile.Name.EndsWith(
                    [string]$forbiddenExtension,
                    [System.StringComparison]::OrdinalIgnoreCase)) `
            -Message "Forbidden package-root file: '$($rootFile.Name)'."
    }
}

foreach ($file in @(Get-ChildItem -LiteralPath $resolvedPackageRoot -Recurse -File -Force)) {
    $relativePath = [System.IO.Path]::GetRelativePath($resolvedPackageRoot, $file.FullName)
    Assert-ReleaseCondition `
        -Condition (-not (Test-ForbiddenRelativePath -RelativePath $relativePath)) `
        -Message "Forbidden package file: '$relativePath'."
}

$licensesRoot = Join-Path $resolvedPackageRoot 'Licenses'
Assert-ReleaseCondition `
    -Condition (Test-Path -LiteralPath $licensesRoot -PathType Container) `
    -Message 'Licenses directory is missing.'
Assert-ReleaseCondition `
    -Condition (@(Get-ChildItem -LiteralPath $licensesRoot -Recurse -File).Count -gt 0) `
    -Message 'Licenses directory is empty.'
Assert-ReleaseCondition `
    -Condition (Test-Path -LiteralPath (Join-Path $licensesRoot 'THIRD-PARTY-NOTICES.txt') -PathType Leaf) `
    -Message 'Licenses/THIRD-PARTY-NOTICES.txt is missing.'

$executablePath = Join-Path $resolvedPackageRoot ([string]$manifest.executableName)
$versionInfo = (Get-Item -LiteralPath $executablePath).VersionInfo
Assert-ReleaseCondition `
    -Condition ($versionInfo.FileVersion.StartsWith(
            [string]$manifest.fileVersion,
            [System.StringComparison]::OrdinalIgnoreCase)) `
    -Message "Executable FileVersion '$($versionInfo.FileVersion)' does not match '$($manifest.fileVersion)'."
Assert-ReleaseCondition `
    -Condition ($versionInfo.ProductVersion.StartsWith(
            $ExpectedVersion,
            [System.StringComparison]::OrdinalIgnoreCase)) `
    -Message "Executable ProductVersion '$($versionInfo.ProductVersion)' does not match '$ExpectedVersion'."

Assert-NoSecretLikeContent -Root $resolvedPackageRoot

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $packagePrefix = $resolvedPackageRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    Assert-ReleaseCondition `
        -Condition (-not $resolvedReportPath.StartsWith(
                $packagePrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) `
        -Message 'Verification report must be written outside the package root.'

    $reportDirectory = Split-Path -Parent $resolvedReportPath
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    @(
        "# Release Verification Report — $ExpectedVersion",
        '',
        '- Result: PASS',
        "- Package root: $([System.IO.Path]::GetFileName($resolvedPackageRoot))",
        "- Root allowlist: PASS",
        "- Recursive forbidden-file scan: PASS",
        "- Version metadata: PASS",
        "- License payload: PASS",
        "- Secret/developer-path scan: PASS"
    ) | Set-Content -LiteralPath $resolvedReportPath -Encoding utf8
}

Write-Host "PASS: package matches $ExpectedVersion manifest."
