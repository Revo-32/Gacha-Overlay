param([ValidateRange(1024,65535)][int]$LocalPort = 15188)
$ErrorActionPreference = 'Stop'
$raw = ssh -o BatchMode=yes revo-m8 'docker inspect --format "{{json .NetworkSettings.Networks}}" lsoverlay-core-dev-fixture-1'
if ($LASTEXITCODE -ne 0) { throw '개발 fixture의 주소를 조회하지 못했습니다.' }
$networks = $raw | ConvertFrom-Json
$address = $networks.'lsoverlay-core-dev_isolated'.IPAddress
$parsed = $null
if (![Net.IPAddress]::TryParse($address, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw '예상한 개발 네트워크 주소가 아닙니다.'
}
Write-Host "Core fixture: http://127.0.0.1:$LocalPort (이 창을 유지하고, 종료할 때 Ctrl+C)"
ssh -N -o BatchMode=yes -o ExitOnForwardFailure=yes -L "127.0.0.1:${LocalPort}:${address}:8080" revo-m8
if ($LASTEXITCODE -ne 0) { throw 'SSH 개발 터널이 종료됐거나 포트를 열지 못했습니다.' }
