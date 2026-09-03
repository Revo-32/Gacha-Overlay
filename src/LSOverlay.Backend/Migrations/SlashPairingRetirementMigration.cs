using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LSOverlay.Backend.Configuration;

namespace LSOverlay.Backend.Migrations;

// Migration-only: no registration, interaction handling, credential issuance or
// durable credential changes. Each process becomes inert after verified absence.
internal sealed class SlashPairingRetirementMigration : IDisposable
{
    public const string Version = "M9.15/slash-retirement-v1";
    private readonly BackendConfiguration _configuration;
    private readonly HttpClient _http;
    public bool Completed { get; private set; }

    public SlashPairingRetirementMigration(BackendConfiguration configuration, HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        // A dedicated bounded REST client avoids the SDK 3.20.1 guild-command
        // DeleteAsync implementation dropping its RequestOptions/cancellation.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("https://discord.com/api/v10/"),
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", configuration.Credential.RevealForDiscordLogin());
    }

    // Single caller: the owned background worker. A failed attempt leaves no marker.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Completed) return;
        using var application = await GetAsync("oauth2/applications/@me", cancellationToken).ConfigureAwait(false);
        var applicationId = ReadId(application.RootElement, "id");
        if (applicationId == 0) throw new InvalidDataException("Application identity unavailable.");
        var route = $"applications/{applicationId}/guilds/{_configuration.TargetGuildId}/commands";
        using var commands = await GetAsync(route, cancellationToken).ConfigureAwait(false);
        if (commands.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Command list invalid.");
        foreach (var command in commands.RootElement.EnumerateArray())
        {
            if (!IsOwnedName(command, applicationId)) continue;
            // Refuse changed/ambiguous command shapes instead of deleting another feature.
            if (!IsExactLegacyShape(command)) throw new InvalidDataException("Legacy command shape differs; review required.");
            var commandId = ReadId(command, "id");
            if (commandId == 0) throw new InvalidDataException("Command identity invalid.");
            using var response = await _http.DeleteAsync($"{route}/{commandId}", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        }
        using var remaining = await GetAsync(route, cancellationToken).ConfigureAwait(false);
        if (remaining.RootElement.ValueKind != JsonValueKind.Array ||
            remaining.RootElement.EnumerateArray().Any(command => IsOwnedName(command, applicationId)))
            throw new InvalidDataException("Legacy command absence not confirmed.");
        Completed = true;
    }

    private bool IsOwnedName(JsonElement command, ulong applicationId) =>
        ReadId(command, "application_id") == applicationId &&
        (!command.TryGetProperty("guild_id", out _) || ReadId(command, "guild_id") == _configuration.TargetGuildId) &&
        command.TryGetProperty("type", out var type) && type.TryGetInt32(out var kind) && kind == 1 &&
        command.TryGetProperty("name", out var name) && name.GetString() == "lsoverlay";

    internal static bool IsExactLegacyShape(JsonElement command)
    {
        if (!command.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() != 1) return false;
        var pair = options[0];
        if (!IsOption(pair, "pair", 1) || !pair.TryGetProperty("options", out var fields) ||
            fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() != 1) return false;
        return IsOption(fields[0], "code", 3) && fields[0].TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True;
    }

    private static bool IsOption(JsonElement option, string name, int type) =>
        option.TryGetProperty("name", out var n) && n.GetString() == name &&
        option.TryGetProperty("type", out var t) && t.TryGetInt32(out var value) && value == type;

    private static ulong ReadId(JsonElement value, string key) =>
        value.TryGetProperty(key, out var id) && id.ValueKind == JsonValueKind.String && ulong.TryParse(id.GetString(), out var parsed) ? parsed : 0;

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public void Dispose() => _http.Dispose();
}
