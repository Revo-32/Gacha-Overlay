[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 5191)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$taskProject = Join-Path $taskRoot 'tools/dev/LSOverlay.PublicPreview/LSOverlay.PublicPreview.csproj'
Write-Host "Offline only. No Discord credential or running Backend is required."
& dotnet run --project $taskProject -c Release -- $taskRoot $Port
if ($LASTEXITCODE -ne 0) { throw "Offline preview exited with code $LASTEXITCODE. If port $Port is busy, use -Port with a free port." }
