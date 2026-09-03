[CmdletBinding()]
param([string]$RepositoryRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Join-Path $PSScriptRoot '../..' }
$taskRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$taskInputs = @(
    'web/status/Dockerfile', 'web/status/Dockerfile.dockerignore', 'web/status/entrypoint.sh',
    'web/status/public/index.html', 'web/status/public/styles.css', 'web/status/public/status.js',
    'assets/branding/LS_Overlay_logo.png'
)
$taskRules = @(Get-Content -LiteralPath (Join-Path $taskRoot 'web/status/Dockerfile.dockerignore') | Where-Object { $_ -and -not $_.StartsWith('#') })
if ($taskRules[0] -cne '**') { throw 'Status context must deny all by default.' }
$taskAllowed = @($taskRules | Select-Object -Skip 1 | ForEach-Object {
    if (-not $_.StartsWith('!') -or $_.Substring(1) -match '[*?]') { throw 'Only exact allowlisted status inputs are allowed.' }
    $_.Substring(1)
})
if (@(Compare-Object ($taskInputs | Sort-Object) ($taskAllowed | Sort-Object)).Count) { throw 'Unexpected status context allowlist.' }
$taskDocker = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'web/status/Dockerfile')
foreach ($line in @('FROM busybox:1.37.0', 'COPY web/status/public/ /www/', 'COPY assets/branding/LS_Overlay_logo.png /www/assets/ls-overlay-logo.png', 'COPY web/status/entrypoint.sh /entrypoint.sh', 'USER 10001:10001')) {
    if (-not $taskDocker.Contains($line)) { throw 'Unexpected status runtime recipe.' }
}
if (@($taskDocker -split "`n" | Where-Object { $_ -match '^COPY ' }).Count -ne 3) { throw 'Additional runtime COPY rejected.' }
$taskEntry = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'web/status/entrypoint.sh')
if (-not $taskEntry.Contains('${PORT:-8080}') -or -not $taskEntry.Contains('0.0.0.0:$status_port') -or $taskEntry.Contains("`r")) { throw 'PORT, binding or shell line ending is invalid.' }
$taskStage = Join-Path ([IO.Path]::GetTempPath()) ('LSOverlay-M101-StatusContext-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage | Out-Null
foreach ($relative in $taskInputs) {
    $source = Join-Path $taskRoot $relative
    $cursor = Get-Item -LiteralPath $source
    while ($null -ne $cursor -and $cursor.FullName -ne $taskRoot) {
        if ($cursor.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Linked status input rejected.' }
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
    $destination = Join-Path $taskStage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
$taskRuntime = Join-Path $taskStage 'runtime-check'
New-Item -ItemType Directory -Path (Join-Path $taskRuntime 'assets') -Force | Out-Null
foreach ($name in @('index.html', 'styles.css', 'status.js')) {
    Copy-Item -LiteralPath (Join-Path $taskStage "web/status/public/$name") -Destination (Join-Path $taskRuntime $name)
}
Copy-Item -LiteralPath (Join-Path $taskStage 'assets/branding/LS_Overlay_logo.png') -Destination (Join-Path $taskRuntime 'assets/ls-overlay-logo.png')
$taskFiles = @(Get-ChildItem -LiteralPath $taskRuntime -Recurse -File)
if ($taskFiles.Count -ne 4) { throw 'Unexpected static runtime contents.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskRuntime 'assets/ls-overlay-logo.png')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskRoot 'assets/branding/LS_Overlay_logo.png')).Hash) { throw 'Logo bytes changed.' }
$taskDockerStatus = 'NOT RUN - Docker CLI unavailable'
if (Get-Command docker -ErrorAction SilentlyContinue) {
    & docker build --file (Join-Path $taskStage 'web/status/Dockerfile') --tag ls-overlay-status:m101-local $taskStage
    if ($LASTEXITCODE -ne 0) { throw 'Status Docker image build failed.' }
    $taskDockerStatus = 'PASS'
}
[ordered]@{ SourceClosure='PASS'; IsolatedStaticRuntime='PASS'; ContextInputs=$taskInputs.Count; RuntimeFiles=$taskFiles.Count; LogoBytes='UNCHANGED'; DockerImageBuild=$taskDockerStatus; StagingDirectory=$taskStage } | ConvertTo-Json
