[CmdletBinding()]
param([switch]$Candidate, [ValidateSet('core-release','core-cleanup')][string]$Stage='core-release')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if($Stage -eq 'core-cleanup' -and !$Candidate){throw 'Cleanup build is a validation candidate, not a replacement for the public release'}
$build=Join-Path $root "artifacts/core/$Stage/native/Release"
$version='1.0.0'
$exe=Join-Path $build 'LSOverlayCore.exe'
if((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $version){throw 'Native product version mismatch'}
Push-Location $root
try {
    $sourceCommit=(& git rev-parse HEAD).Trim()
    $dirty=[bool](& git status --porcelain)
    if($dirty -and !$Candidate){throw 'Final packaging requires a clean committed source tree'}
    $suffix=if($Stage -eq 'core-cleanup'){'-cleanup-candidate'}elseif($Candidate){'-candidate'}else{''}
    $name="LS-Overlay-Core-$version-win-x64$suffix"
    $output=Join-Path $root "artifacts/core/release/$name"
    $zip="$output.zip"
    if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $zip)){throw 'Refusing to overwrite an existing package'}
    New-Item -ItemType Directory -Path $output | Out-Null
    Copy-Item -LiteralPath $exe -Destination $output
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $root 'native/LSOverlayCore/third-party/yyjson-LICENSE.txt') -Destination $output
    Copy-Item -LiteralPath (Join-Path $root 'native/LSOverlayCore/RELEASE-README.ko.txt') -Destination (Join-Path $output 'README.ko.txt')
    $fonts=@('Cafe24PROSlimFit.ttf','Cafe24PROSlimMax.ttf','ChosunGu.TTF','KIMM_Bold.ttf','KIMM_Light.ttf','PretendardVariable.ttf','WantedSansVariable.ttf')
    New-Item -ItemType Directory -Path (Join-Path $output 'fonts/ThirdPartyNotices') | Out-Null
    foreach($font in $fonts){Copy-Item -LiteralPath (Join-Path $build "fonts/$font") -Destination (Join-Path $output 'fonts')}
    foreach($notice in @('NOTICE-Fonts.txt','OFL-1.1.txt','License-Cafe24-PRO-Slim-Fit.pdf','License-Cafe24-PRO-Slim-Max.pdf')){
        Copy-Item -LiteralPath (Join-Path $build "fonts/ThirdPartyNotices/$notice") -Destination (Join-Path $output 'fonts/ThirdPartyNotices')
    }
    $metadata=[ordered]@{product='LS Overlay Core';version=$version;sourceCommit=$sourceCommit;uncommittedCandidate=$dirty;architecture='win-x64';signed=$false}
    [IO.File]::WriteAllText((Join-Path $output 'build.json'),($metadata | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $files=Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName
    if($files.Count -ne 16){throw 'Unexpected package file count'}
    $hashes=@($files | ForEach-Object {((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/')})
    [IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'),$hashes,[Text.UTF8Encoding]::new($false))
    Compress-Archive -LiteralPath $output -DestinationPath $zip -CompressionLevel Optimal
    $zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zip.sha256",$zipHash+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    [ordered]@{zip=$zip;sha256=(Get-FileHash -LiteralPath $zip).Hash;bytes=(Get-Item -LiteralPath $zip).Length;files=17;sourceCommit=$sourceCommit;candidate=[bool]$Candidate} | ConvertTo-Json
}finally{Pop-Location}
