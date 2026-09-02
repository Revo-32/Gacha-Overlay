using System.Net;
using Discord.Net;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Presence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Runtime;

internal sealed class DiscordBackendWorker : BackgroundService
{
    private readonly IDiscordGatewayLifecycle _gateway;
    private readonly BackendConfiguration _configuration;
    private readonly BackendConnectionHealth _health;
    private readonly BackendMetrics _metrics;
    private readonly ILogger<DiscordBackendWorker> _logger;

    public DiscordBackendWorker(
        IDiscordGatewayLifecycle gateway,
        BackendConfiguration configuration,
        BackendConnectionHealth health,
        BackendMetrics metrics,
        ILogger<DiscordBackendWorker> logger)
    {
        _gateway = gateway;
        _configuration = configuration;
        _health = health;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health.Transition(
            BackendConnectionHealthState.Starting,
            BackendConnectionHealthReason.Startup);
        _logger.LogInformation("LS Overlay Backend: Starting");
        _logger.LogInformation("Discord SDK: Discord.Net.WebSocket 3.20.1");
        _logger.LogInformation("Target Guild: Configured");
        _logger.LogInformation(
            "Session Hosts Configured: {Count}",
            _configuration.SessionHostIds.Count);
        _logger.LogInformation(
            "Authoritative Sales Window: {Count}",
            GachaOverlay.Core.Sales.AuthoritativeSalesWindow.Size);
        _logger.LogInformation(
            "GTA Presence profile: {Label}; structured party only",
            GtaPresenceProfile.Enhanced.DisplayLabel);

        try
        {
            await _gateway.StartAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.Unauthorized)
        {
            _health.Transition(
                BackendConnectionHealthState.Faulted,
                BackendConnectionHealthReason.AuthenticationFailed);
            _logger.LogError(
                "Discord authentication failed category={Category}; verify the Bot token.",
                exception.GetType().Name);
            throw;
        }
        catch (Exception exception)
        {
            _health.Transition(
                BackendConnectionHealthState.Faulted,
                BackendConnectionHealthReason.UnexpectedFailure);
            _logger.LogError(
                "Backend runtime failed category={Category}.",
                exception.GetType().Name);
            throw;
        }
        finally
        {
            await _gateway.StopAsync().ConfigureAwait(false);
            LogFinalCounters(_metrics.Snapshot());
            _health.Transition(
                BackendConnectionHealthState.Stopped,
                BackendConnectionHealthReason.GracefulShutdown);
            _logger.LogInformation("Discord: Stopped");
        }
    }

    private void LogFinalCounters(BackendMetricsSnapshot snapshot)
    {
        _logger.LogInformation(
            "Backend counters: ready={Ready} messages={Messages} reactions={Reactions} " +
            "presenceReceived={PresenceReceived} presenceChanges={PresenceChanges}.",
            snapshot.DiscordReady,
            snapshot.MessageCreate + snapshot.MessageUpdate + snapshot.MessageDelete,
            snapshot.ReactionAdd + snapshot.ReactionRemove + snapshot.ReactionClear,
            snapshot.PresenceReceived,
            snapshot.PresenceNormalizedChange);
    }
}
