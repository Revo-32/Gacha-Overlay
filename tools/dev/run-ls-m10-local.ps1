[CmdletBinding()]
param(
    [string]$PublishedExe = '',
    [switch]$OfflineCheck
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PublishedExe) { $PublishedExe = Join-Path $PSScriptRoot '../../artifacts/m10-completed-only/wpf-win-x64/GachaOverlay.App.exe' }
$exe = (Resolve-Path -LiteralPath $PublishedExe).Path
if ([IO.Path]::GetFileName($exe) -ne 'GachaOverlay.App.exe') { throw 'Select the published GachaOverlay.App.exe.' }
if ($OfflineCheck) {
    & (Join-Path $PSScriptRoot 'run-ls-m9141-client-check.ps1') -PublishedExe $exe
    return
}
if (@(Get-Process -Name 'GachaOverlay.App' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close the existing Overlay from its tray menu, then run this helper again. No processes were stopped.'
}
Write-Host 'LS Overlay M10.0 - completion-only main Sales bar correction'
Write-Host 'Uses the existing Windows profile and protected credential. Does not start a Backend or request operator secrets.'
Write-Host 'The built-in service address is https://overlay.revo32.cloud . Developer overrides remain in advanced Settings.'
Write-Host '[ ] Existing credential reconnects, or browser Discord sign-in succeeds.'
Write-Host '[ ] F9 visibility; F10 lock/unlock; unlocked gear, drag and resize.'
Write-Host '[ ] Session: selected host only; normal count /30; full session badge (if host is available).'
Write-Host '[ ] Scroll history, receive a message, then jump to latest. Scroll over transparent gaps.'
Write-Host '[ ] Channel list/order, channel switch feedback, optional previous/next shortcuts.'
Write-Host '[ ] GTA5 Enhanced foreground: press/release T; Discord opens once without a stray t.'
Write-Host '[ ] Ctrl/Shift/Alt/Win+T and T outside GTA are unaffected; absent Discord is safe.'
Write-Host '[ ] Compact sales aliases/emoji/quantity, uncertain detail text, detail age and own-row accents.'
Write-Host '[ ] The main Sales bar offers only completion for my first active post, even with details collapsed; detail rows have no action buttons.'
Write-Host '[ ] Completed sale stays pending until confirmed; confirmed row leaves; failed request stays.'
Write-Host '[ ] Expanded detail remains visible while locked and all mouse input passes through.'
Write-Host '[ ] Settings, themes, media, notifications and diagnostic export remain healthy.'
Write-Host 'Launch is not PASS. Report actual results; unavailable session-host testing is NOT RUN.'
$process = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru
Write-Host "Client started (PID $($process.Id)). Close it from the tray after validation."
$process.Dispose()
