param()

$ErrorActionPreference = 'Stop'

$runningDiscord = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -in @('Discord', 'DiscordPTB', 'DiscordCanary') }
if ($runningDiscord) {
    Write-Error 'Discord가 실행 중입니다. 시스템 트레이에서도 Discord를 완전히 종료한 뒤 다시 실행하세요.'
    exit 1
}

$updater = Join-Path $env:LOCALAPPDATA 'Discord\Update.exe'
if (-not (Test-Path -LiteralPath $updater)) {
    Write-Error "Discord Update.exe를 찾지 못했습니다: $updater"
    exit 2
}

Start-Process -FilePath $updater -ArgumentList @(
    '--processStart',
    'Discord.exe',
    '--process-start-args',
    '--force-renderer-accessibility'
)

Write-Host 'Discord를 --force-renderer-accessibility 옵션으로 시작했습니다.'
Write-Host 'Discord에서 #🚒판매모집 채널을 직접 선택한 뒤 Gacha Overlay를 실행하세요.'
