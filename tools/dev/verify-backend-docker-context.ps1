#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [switch]$CheckOnly,
    [switch]$RequireTracked
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Captured {
    param([string]$Executable, [string[]]$Arguments)
    $output = @(& $Executable @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine)
}

function Get-RelativeInput {
    param([string]$Root, [string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($Root, $full).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('../')) {
        throw "Build input escapes its source root: $Path"
    }
    return $relative
}

function Get-ProjectClosure {
    param([string]$Root, [string]$EntryProject)
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($EntryProject)
    $projects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($queue.Count -gt 0) {
        $relative = $queue.Dequeue()
        if (-not $projects.Add($relative)) { continue }
        $project = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing project: $relative" }
        $evaluated = Invoke-Captured dotnet @(
            'msbuild', $project, '-nologo', '-p:Configuration=Release',
            '-getProperty:TargetFramework,TargetFrameworks,UseWPF,UseWindowsForms',
            '-getItem:ProjectReference,Compile,Content,EmbeddedResource,AdditionalFiles'
        ) | ConvertFrom-Json
        if ($evaluated.Properties.TargetFramework -match '-windows' -or
            $evaluated.Properties.TargetFrameworks -match '-windows' -or
            $evaluated.Properties.UseWPF -eq 'true' -or $evaluated.Properties.UseWindowsForms -eq 'true') {
            throw "Windows/WPF project in Backend closure: $relative"
        }
        [void]$inputs.Add($relative)
        foreach ($reference in $evaluated.Items.ProjectReference) {
            $queue.Enqueue((Get-RelativeInput $Root $reference.FullPath))
        }
        foreach ($kind in @('Compile', 'Content', 'EmbeddedResource', 'AdditionalFiles')) {
            foreach ($item in $evaluated.Items.$kind) {
                [void]$inputs.Add((Get-RelativeInput $Root $item.FullPath))
            }
        }
    }
    return [pscustomobject]@{ Projects = @($projects | Sort-Object); Inputs = @($inputs | Sort-Object) }
}

function Read-DockerRules {
    param([string]$Path)
    foreach ($line in Get-Content -LiteralPath $Path) {
        $value = $line.Trim()
        if (-not $value -or $value.StartsWith('#')) { continue }
        $include = $value.StartsWith('!')
        $pattern = $value.TrimStart('!').Trim('/')
        # Deliberately support the recipe's glob subset, not a pretend full Docker
        # parser. Unknown escaping/character classes require a verifier update.
        if ($pattern -match '[\[\]\\]' -or $pattern.Contains('***') -or -not $pattern) {
            throw "Unsupported dockerignore pattern: $value"
        }
        $regex = [regex]::Escape($pattern).Replace('\*\*/', '(?:.*/)?')
        $regex = $regex.Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
        [pscustomobject]@{ Include = $include; Regex = [regex]::new('^' + $regex + '$') }
    }
}

function Test-DockerIncluded {
    param([string]$Path, [object[]]$Rules)
    $included = $true
    # Docker directory matches also affect descendants. Ignoring this would
    # incorrectly consider a broad !src/ exception to exclude WPF source.
    $ancestors = [Collections.Generic.List[string]]::new()
    $current = $Path
    while ($current) {
        $ancestors.Add($current)
        $separator = $current.LastIndexOf('/')
        $current = if ($separator -lt 0) { '' } else { $current.Substring(0, $separator) }
    }
    foreach ($rule in $Rules) {
        foreach ($candidate in $ancestors) {
            if ($rule.Regex.IsMatch($candidate)) { $included = $rule.Include; break }
        }
    }
    return $included
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$entry = 'src/LSOverlay.Backend/LSOverlay.Backend.csproj'
$recipe = (Get-Content -LiteralPath (Join-Path $root 'Dockerfile') -Raw) -replace '\\\r?\n', ' '
$lines = @($recipe -split '\r?\n')
$restore = -1
$publish = -1
$copies = [Collections.Generic.List[object]]::new()
$buildStage = $false
for ($index = 0; $index -lt $lines.Count; $index++) {
    $line = $lines[$index].Trim()
    if ($line -match '^FROM ') { $buildStage = $line -match '\sAS\sbuild$'; continue }
    if (-not $buildStage) { continue }
    if ($line -match '^RUN dotnet restore (\S+)') {
        if ($Matches[1] -cne $entry) { throw 'Docker restore target is not Backend.' }
        $restore = $index
    }
    if ($line -match '^RUN dotnet publish (\S+)') {
        if ($Matches[1] -cne $entry) { throw 'Docker publish target is not Backend.' }
        $publish = $index
    }
    if ($line -match '^COPY (.+)$') {
        $tokens = @($Matches[1] -split '\s+')
        if ($tokens.Count -lt 2 -or $line -match '[\[\]"*?]' -or $tokens[0].StartsWith('--')) {
            throw 'Unsupported build-stage COPY syntax; update verifier before changing the recipe.'
        }
        foreach ($source in $tokens[0..($tokens.Count - 2)]) {
            $copies.Add([pscustomobject]@{ Source = $source; Destination = $tokens[-1]; Line = $index })
        }
    }
}
if ($restore -lt 0 -or $publish -le $restore) { throw 'Expected Backend restore followed by publish.' }

$graph = Get-ProjectClosure $root $entry
$projectDirs = @($graph.Projects | ForEach-Object { ($_ -replace '/[^/]+$', '') + '/' })
$metadata = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($project in $graph.Projects) {
    $directory = ($project -replace '/[^/]+$', '') + '/'
    $projectCopy = @($copies | Where-Object {
        $_.Source -ceq $project -and $_.Destination -ceq $directory -and $_.Line -lt $restore
    })
    $sourceCopy = @($copies | Where-Object {
        $_.Source -ceq $directory -and $_.Destination -ceq $directory -and
        $_.Line -gt $restore -and $_.Line -lt $publish
    })
    if ($projectCopy.Count -ne 1 -or $sourceCopy.Count -ne 1) {
        throw "Complete project-level COPY missing from Dockerfile: $directory"
    }
}
foreach ($copy in $copies) {
    if ($graph.Projects -ccontains $copy.Source -or $projectDirs -ccontains $copy.Source) { continue }
    if ($copy.Source.Contains('/') -or $copy.Source.Contains('\') -or $copy.Destination -cne './' -or
        $copy.Line -ge $restore -or $copy.Source -notmatch '^(global\.json|Directory\.(Build\.(props|targets)|Packages\.props)|[Nn]u[Gg]et\.[Cc]onfig)$') {
        throw "Unexpected Docker build input: $($copy.Source)"
    }
    [void]$metadata.Add($copy.Source)
}
foreach ($name in @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.config', 'nuget.config')) {
    if ((Test-Path -LiteralPath (Join-Path $root $name)) -and -not $metadata.Contains($name)) {
        throw "Root build metadata missing from Dockerfile: $name"
    }
}

$gitArgs = @('-C', $root, '-c', 'core.quotepath=false', 'ls-files', '--cached')
if (-not $RequireTracked) { $gitArgs += @('--others', '--exclude-standard') }
$gitFiles = (Invoke-Captured git $gitArgs) -split '\r?\n' | Where-Object {
    $_ -and (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf)
}
$eligible = [Collections.Generic.HashSet[string]]::new([string[]]$gitFiles, [StringComparer]::Ordinal)
$rules = @(Read-DockerRules (Join-Path $root '.dockerignore'))
$required = @($graph.Inputs) + @($metadata) + @('Dockerfile', '.dockerignore')
foreach ($path in $required) {
    if (-not $eligible.Contains($path)) { throw "Required source is absent from Git candidates (ignored/untracked): $path" }
    if (-not (Test-DockerIncluded $path $rules)) { throw "Required source excluded by .dockerignore: $path" }
}
$selected = @($eligible | Where-Object { Test-DockerIncluded $_ $rules } | Sort-Object)
foreach ($path in $selected) {
    $expected = $metadata.Contains($path) -or $path -cin @('Dockerfile', '.dockerignore')
    foreach ($directory in $projectDirs) { $expected = $expected -or $path.StartsWith($directory, [StringComparison]::Ordinal) }
    if (-not $expected) { throw "Docker context includes a file outside Backend closure: $path" }
    if ($path -match '(^|/)(bin|obj|state|data|logs|tmp|publish|TestResults|\.git)/|(^|/)\.env|\.(dat|log|csv|zip|bak|pfx|pem|key)$') {
        throw "Runtime/secret artifact allowed into Docker context: $path"
    }
    # Reject links/junctions before Copy-Item can resolve content from another tree.
    $file = Get-Item -LiteralPath (Join-Path $root $path)
    $cursor = $file
    while ($null -ne $cursor -and $cursor.FullName -ne $root) {
        if ($cursor.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "Linked build input rejected: $path" }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
}

$report = [ordered]@{
    Mode = $(if ($RequireTracked) { 'Git-index-only' } else { 'Tracked plus non-ignored pending files; commit all required sources' })
    Projects = $graph.Projects
    RequiredInputCount = $required.Count
    ContextFileCount = $selected.Count
    SourceClosure = 'PASS'
    IsolatedRestore = 'NOT RUN'
    IsolatedPublish = 'NOT RUN'
    DockerImageBuild = 'NOT RUN'
}
if (-not $CheckOnly) {
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('LSOverlay-DockerContext-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($path in $selected) {
        $destination = Join-Path $stage $path
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $path) -Destination $destination
    }
    # Re-evaluate inside staging. Absolute Compile/ProjectReference links back to
    # the original checkout fail here rather than lending missing source files.
    $stagedGraph = Get-ProjectClosure $stage $entry
    if (@(Compare-Object $graph.Projects $stagedGraph.Projects).Count -gt 0 -or
        @(Compare-Object $graph.Inputs $stagedGraph.Inputs).Count -gt 0) {
        throw 'Staged project/source closure differs from the working-tree graph.'
    }
    Push-Location $stage
    try {
        Write-Host (Invoke-Captured dotnet @('restore', $entry, '--verbosity', 'minimal'))
        $report.IsolatedRestore = 'PASS'
        Write-Host (Invoke-Captured dotnet @('publish', $entry, '-c', 'Release', '--no-restore',
            '--self-contained', 'false', '-p:UseAppHost=false', '-o', (Join-Path $stage 'publish'), '--verbosity', 'minimal'))
        $report.IsolatedPublish = 'PASS'
    }
    finally { Pop-Location }
    $files = @(Get-ChildItem -LiteralPath (Join-Path $stage 'publish') -Recurse -File)
    $report.StagingDirectory = $stage
    $report.PublishedFileCount = $files.Count
    $report.PublishedBytes = ($files | Measure-Object -Property Length -Sum).Sum
    # Keep this unique source/output directory for inspection; never delete user paths.
}
$report | ConvertTo-Json -Depth 4
