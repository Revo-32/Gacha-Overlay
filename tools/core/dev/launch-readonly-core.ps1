[CmdletBinding()]
param([switch]$SalesActions)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$stage = if ($SalesActions) { 'm7-sales-polish' } else { 'm6-hud-polish' }
$executable = Join-Path $repository "artifacts/core/$stage/native/Release/LSOverlayCore.exe"
if (!(Test-Path -LiteralPath $executable)) { throw 'Build the current Core native client first.' }
if (Get-Process -Name LSOverlayCore -ErrorAction SilentlyContinue) { throw '먼저 실행 중인 Core를 트레이에서 종료한 뒤 다시 실행해 주세요. Full 앱은 종료하지 않아도 됩니다.' }
$observation = Join-Path $repository ("artifacts/core/$stage/live-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $observation | Out-Null
$tunnel = $null
$listener = Get-NetTCPConnection -State Listen -LocalPort 15190 -ErrorAction SilentlyContinue
try {
    if (!$listener) {
        $tunnel = Start-Process -FilePath ssh.exe -ArgumentList @('-o','BatchMode=yes','-o','ExitOnForwardFailure=yes','-o','ServerAliveInterval=15','-o','ServerAliveCountMax=2','-N','-L','127.0.0.1:15190:127.0.0.1:15190','revo-m8') -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $observation 'tunnel-error.txt')
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 200
            if ($tunnel.HasExited) { throw 'Development SSH tunnel failed. Production was not changed.' }
            $listener = Get-NetTCPConnection -State Listen -LocalPort 15190 -ErrorAction SilentlyContinue
        } while (!$listener -and [DateTime]::UtcNow -lt $deadline)
    }
    if (!$listener -or @($listener | Where-Object LocalAddress -ne '127.0.0.1').Count -ne 0) { throw 'Expected private loopback listener is unavailable.' }
    # Native code checks the read-only manifest before authentication. It contacts
    # the existing HTTPS issuer directly and uses only its own DPAPI store.
    # F9/F10 are active for normal use; native registration reports conflicts.
    Write-Output 'Core 개발 연결을 시작합니다. 브라우저가 열리면 Discord 로그인을 승인해 주세요.'
    Write-Output "실행 파일: $executable"
    Write-Output 'Full 앱의 로그인·설정은 변경하지 않습니다.'
    if ($SalesActions) { Write-Output '판매 조작 활성: 본인 글의 판매 완료·완료 취소 버튼은 확인창 없이 클릭 즉시 실제 Discord 상태를 변경합니다.' }
    else { Write-Output '판매 조작/Discord 쓰기는 비활성입니다.' }
    Write-Output 'F9: 표시/숨김 · F10: 잠금/해제 · Ctrl+Alt+←/→: 채팅방 이동 (설정 → HUD에서 변경 가능)'
    Write-Output "개인정보 없는 연결 계측: $observation"
    $clientArguments = @('--core-dev-endpoint','http://127.0.0.1:15190','--core-dev-observe',('"' + $observation + '"'))
    if ($SalesActions) { $clientArguments += '--sales-actions' }
    $client = Start-Process -FilePath $executable -ArgumentList $clientArguments -PassThru -WindowStyle Hidden
    $client.WaitForExit()
} finally {
    if ($tunnel -and !$tunnel.HasExited) { $tunnel.Kill() } # Only this script's child SSH process.
}
