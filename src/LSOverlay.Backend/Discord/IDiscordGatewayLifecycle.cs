namespace LSOverlay.Backend.Discord;

internal interface IDiscordGatewayLifecycle
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}
