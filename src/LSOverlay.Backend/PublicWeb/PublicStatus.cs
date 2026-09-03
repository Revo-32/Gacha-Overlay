using System.Text.Json;
using System.Text.Json.Serialization;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Backend.WebAuth;

namespace LSOverlay.Backend.PublicWeb;

[JsonConverter(typeof(PublicStatusStateConverter))]
internal enum PublicStatusState { Operational, Degraded, Maintenance, Unavailable, Unknown }

internal sealed class PublicStatusStateConverter() :
    JsonStringEnumConverter<PublicStatusState>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

// Explicit, closed public contract. Never add internal health objects, reasons,
// configuration, identities, exception text or per-client metrics here.
internal sealed record PublicServiceStates(
    PublicStatusState Backend,
    PublicStatusState Discord,
    PublicStatusState Authentication,
    PublicStatusState Remote);

internal sealed record PublicStatusSnapshot(
    int SchemaVersion,
    PublicStatusState Overall,
    DateTimeOffset UpdatedAt,
    PublicServiceStates Services);

internal sealed record PublicReadiness(
    bool Started,
    bool Stopping,
    BackendConnectionHealthState Gateway,
    bool AuthenticationConfigured,
    bool CredentialStorageAvailable,
    bool RemoteHostAvailable,
    bool RemoteCapacityAvailable,
    bool AuthenticationCapacityAvailable = true);

internal sealed class PublicStatusService(
    BackendConfiguration configuration,
    BackendConnectionHealth health,
    ClientCredentialRegistry credentials,
    IHostApplicationLifetime lifetime,
    IServiceProvider services)
{
    // Only local readiness, not an external OAuth probe or a claim that every
    // client/channel/media/CDN/write-back operation currently succeeds.
    public PublicStatusSnapshot Capture() => Map(new PublicReadiness(
        lifetime.ApplicationStarted.IsCancellationRequested,
        lifetime.ApplicationStopping.IsCancellationRequested,
        health.Current.State,
        configuration.WebAuth is not null && services.GetService<DiscordWebAuthService>() is not null,
        !credentials.IsFaulted,
        services.GetService<BackendWebSocketSession>() is not null &&
            services.GetService<RemotePublicationHub>() is not null,
        services.GetService<RemoteConnectionLimiter>()?.HasCapacity == true,
        services.GetService<DiscordWebAuthService>()?.HasCapacity == true), DateTimeOffset.UtcNow);

    internal static PublicStatusSnapshot Map(PublicReadiness input, DateTimeOffset now)
    {
        var running = input.Started && !input.Stopping;
        var backend = input.Stopping ? PublicStatusState.Unavailable :
            input.Started ? PublicStatusState.Operational : PublicStatusState.Unknown;
        var discord = input.Gateway switch
        {
            BackendConnectionHealthState.Ready when running => PublicStatusState.Operational,
            BackendConnectionHealthState.Connecting or BackendConnectionHealthState.Disconnected => PublicStatusState.Degraded,
            BackendConnectionHealthState.TargetGuildUnavailable or BackendConnectionHealthState.Faulted or
                BackendConnectionHealthState.Stopped => PublicStatusState.Unavailable,
            _ => PublicStatusState.Unknown,
        };
        var auth = !running || !input.AuthenticationConfigured || !input.CredentialStorageAvailable
            ? PublicStatusState.Unavailable
            : discord == PublicStatusState.Operational && input.AuthenticationCapacityAvailable
                ? PublicStatusState.Operational : PublicStatusState.Degraded;
        var remote = !running || !input.RemoteHostAvailable || !input.CredentialStorageAvailable
            ? PublicStatusState.Unavailable
            : !input.RemoteCapacityAvailable || discord != PublicStatusState.Operational
                ? PublicStatusState.Degraded : PublicStatusState.Operational;
        var states = new PublicServiceStates(backend, discord, auth, remote);
        return new(1, Aggregate(backend, discord, auth, remote), now.ToUniversalTime(), states);
    }

    internal static PublicStatusState Aggregate(params PublicStatusState[] states)
    {
        // No automatic maintenance producer in M10.1; the value is reserved.
        foreach (var severity in new[] { PublicStatusState.Unavailable, PublicStatusState.Maintenance,
            PublicStatusState.Degraded, PublicStatusState.Unknown })
            if (states.Contains(severity)) return severity;
        return states.Length == 0 ? PublicStatusState.Unknown : PublicStatusState.Operational;
    }
}
