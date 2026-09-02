using LSOverlay.Backend.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Runtime;

internal sealed class RemoteAuthenticationHealthReporter : IHostedService
{
    private readonly ClientCredentialRegistry _credentials;
    private readonly ILogger<RemoteAuthenticationHealthReporter> _logger;

    public RemoteAuthenticationHealthReporter(
        ClientCredentialRegistry credentials,
        ILogger<RemoteAuthenticationHealthReporter> logger)
    {
        _credentials = credentials;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_credentials.IsFaulted)
        {
            _logger.LogWarning(
                "Remote authentication: Unavailable; credential registry failed closed.");
        }
        else
        {
            _logger.LogInformation("Remote authentication: Ready");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
