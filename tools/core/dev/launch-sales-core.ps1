[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Explicit entrypoint so the validation cannot silently select the old read-only build.
& (Join-Path $PSScriptRoot 'launch-readonly-core.ps1') -SalesActions
