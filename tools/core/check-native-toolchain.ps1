$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = $null
if (Test-Path -LiteralPath $vswhere) {
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$compiler = $null
$cmake = $null
if ($installation) {
    $compiler = Get-ChildItem -Path (Join-Path $installation 'VC/Tools/MSVC/*/bin/Hostx64/x64/cl.exe') -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    $candidate = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    if (Test-Path -LiteralPath $candidate) { $cmake = $candidate }
}
if (!$cmake) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source }
$sdkRoot = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10
$sdkHeaders = @()
if ($sdkRoot) {
    $sdkHeaders = @(Get-ChildItem -Path (Join-Path $sdkRoot 'Include/*/um/d2d1.h') -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
}
$ready = [bool]($compiler -and $cmake -and $sdkHeaders.Count -gt 0)
[pscustomobject]@{Ready=$ready;Compiler=$compiler;CMake=$cmake;WindowsSdkHeaders=$sdkHeaders;RequiredWorkload='Desktop development with C++ (MSVC x64/x86, Windows SDK, CMake)'} | ConvertTo-Json -Depth 3
if (!$ready) { exit 2 }
