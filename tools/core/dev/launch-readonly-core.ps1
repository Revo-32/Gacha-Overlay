[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$executable = Join-Path $repository 'artifacts/core/m5/native/Release/LSOverlayCore.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Build the current Core native client first.' }
$observation = Join-Path $repository ('artifacts/core/m6/live-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
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
    # --no-hotkeys deliberately avoids stealing F9/F10 from an open Full client.
    Write-Output 'Core 개발 연결을 시작합니다. 브라우저가 열리면 Discord 로그인을 승인해 주세요.'
    Write-Output 'Full 앱의 로그인·설정은 변경하지 않습니다. 판매 조작/Discord 쓰기는 비활성입니다.'
    Write-Output "개인정보 없는 연결 계측: $observation"
    $client = Start-Process -FilePath $executable -ArgumentList @('--no-hotkeys','--core-dev-endpoint','http://127.0.0.1:15190','--core-dev-observe',('"' + $observation + '"')) -PassThru -WindowStyle Hidden
    $client.WaitForExit()
} finally {
    if ($tunnel -and !$tunnel.HasExited) { $tunnel.Kill() } # Only this script's child SSH process.
}
