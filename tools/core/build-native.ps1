[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('m1','m2','m3','m4')][string]$Stage = 'm4'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install MSVC Build Tools with Desktop development with C++ first.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC C++ workload was not found.' }
$cmake = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ctest = Join-Path (Split-Path $cmake) 'ctest.exe'
if (!(Test-Path -LiteralPath $cmake)) { throw 'Visual Studio CMake component was not found.' }
$buildDirectory = Join-Path $repository "artifacts/core/$Stage/native"
& $cmake -S (Join-Path $repository 'native/LSOverlayCore') -B $buildDirectory -G 'Visual Studio 17 2022' -A x64 "-DCMAKE_GENERATOR_INSTANCE=$installation"
if ($LASTEXITCODE -ne 0) { throw 'Native CMake configuration failed.' }
& $cmake --build $buildDirectory --config $Configuration --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
& $ctest --test-dir $buildDirectory -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Native focused tests failed.' }
Write-Output (Join-Path $buildDirectory "$Configuration/LSOverlayCore.exe")
