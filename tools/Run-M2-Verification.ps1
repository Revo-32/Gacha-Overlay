$ErrorActionPreference = "Stop"
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$projectPath = Join-Path $PSScriptRoot "..\src\GachaOverlay.App\GachaOverlay.App.csproj"

do {
    $clientId = Read-Host "Discord APPLICATION ID"
} until ($clientId -match '^\d+$')

$secureSecret = Read-Host "Discord CLIENT SECRET" -AsSecureString
$secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureSecret)

try {
    $clientSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer)
}

$redirectUri = Read-Host "Redirect URI [Enter = https://127.0.0.1]"
if ([string]::IsNullOrWhiteSpace($redirectUri)) {
    $redirectUri = "https://127.0.0.1"
}

$guildId = Read-Host "Guild ID [Enter = exact target names로 자동 탐색]"

$env:DISCORD_CLIENT_ID = $clientId
$env:DISCORD_CLIENT_SECRET = $clientSecret
$env:DISCORD_REDIRECT_URI = $redirectUri.Trim()

if (-not [string]::IsNullOrWhiteSpace($guildId)) {
    $env:DISCORD_GUILD_ID = $guildId.Trim()
}

$clientSecret = $null
$secureSecret.Dispose()

try {
    & dotnet run --project $projectPath -c Release --no-build
}
finally {
    Remove-Item Env:\DISCORD_CLIENT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:\DISCORD_CLIENT_SECRET -ErrorAction SilentlyContinue
    Remove-Item Env:\DISCORD_REDIRECT_URI -ErrorAction SilentlyContinue
    Remove-Item Env:\DISCORD_GUILD_ID -ErrorAction SilentlyContinue
}
