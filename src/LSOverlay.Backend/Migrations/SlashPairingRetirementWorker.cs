using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Migrations;

// Deliberately not a Gateway callback or health/readiness dependency.
internal sealed class SlashPairingRetirementWorker(
    BackendConfiguration configuration,
    BackendConnectionHealth health,
    ILogger<SlashPairingRetirementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var migration = new SlashPairingRetirementMigration(configuration);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                while (health.Current.State != BackendConnectionHealthState.Ready)
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await migration.RunAsync(deadline.Token).ConfigureAwait(false);
                    logger.LogInformation("Migration {Version}: completed; legacy command absent.", SlashPairingRetirementMigration.Version);
                    return;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Migration {Version}: deferred attempt={Attempt} category={Category}; Remote operation unaffected.",
                        SlashPairingRetirementMigration.Version, attempt, exception.GetType().Name);
                }
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            logger.LogWarning("Migration {Version}: pending; retry on next startup. No legacy authentication handler is active.", SlashPairingRetirementMigration.Version);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
