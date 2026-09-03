# Developer helper only. Capture configured Web OAuth settings, keep them out of
# build/WPF children, and restore them only while spawning the isolated Backend.
function Get-LsWebAuthEnvironment {
    $names = @('LSO_DISCORD_WEB_AUTH_ENABLED', 'LSO_DISCORD_OAUTH_CLIENT_ID',
        'LSO_DISCORD_OAUTH_CLIENT_SECRET', 'LSO_PUBLIC_BASE_URL', 'ASPNETCORE_ENVIRONMENT')
    $values = @{}
    foreach ($name in $names) {
        $values[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    if ($values['LSO_DISCORD_WEB_AUTH_ENABLED'] -ne 'true' -or
        [string]::IsNullOrWhiteSpace($values['LSO_DISCORD_OAUTH_CLIENT_ID']) -or
        [string]::IsNullOrWhiteSpace($values['LSO_DISCORD_OAUTH_CLIENT_SECRET']) -or
        [string]::IsNullOrWhiteSpace($values['LSO_PUBLIC_BASE_URL'])) {
        $values.Clear()
        throw 'M9.15: isolated Backend checks require preconfigured Web OAuth and a registered callback routed to THIS Backend. For normal validation, run the published WPF against the existing production service. No slash fallback exists.'
    }
    return $values
}

function Set-LsWebAuthEnvironment {
    param([hashtable]$Values)
    foreach ($name in $Values.Keys) { [Environment]::SetEnvironmentVariable($name, $Values[$name], 'Process') }
}
