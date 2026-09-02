using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal enum SalesTurnNotificationKind
{
    Next,
    Current,
}

internal interface ISalesNotificationSoundService : IDisposable
{
    void Play(SalesTurnNotificationKind kind, double volumePercent);
}

internal interface ISalesTurnNotificationObserver
{
    void Observe(SalesQueuePresentationState presentation, bool providerHandoff);

    void ResetBaseline();
}

internal sealed class SalesTurnNotificationCoordinator : ISalesTurnNotificationObserver
{
    private readonly object _sync = new();
    private readonly Func<AppSettings> _settings;
    private readonly ISalesNotificationSoundService _sound;
    private readonly IAppLogger _logger;
    private PersonalSalesPosition _lastPosition;
    private bool _hasBaseline;

    public SalesTurnNotificationCoordinator(
        Func<AppSettings> settings,
        ISalesNotificationSoundService sound,
        IAppLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Observe(SalesQueuePresentationState presentation, bool providerHandoff)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        lock (_sync)
        {
            if (!presentation.IsTrustedForNewPersonalAlert)
            {
                return;
            }

            var position = presentation.ContentMode switch
            {
                SalesQueueContentMode.CurrentTurnSelf => PersonalSalesPosition.Current,
                SalesQueueContentMode.NextTurnSelf => PersonalSalesPosition.Next,
                _ => PersonalSalesPosition.Waiting,
            };
            if (!_hasBaseline || providerHandoff)
            {
                _hasBaseline = true;
                _lastPosition = position;
                _logger.Information(
                    "SALES-SOUND",
                    providerHandoff
                        ? "Provider handoff established a silent Sales position baseline."
                        : "Initial Sales position baseline established silently.");
                return;
            }

            if (_lastPosition == position)
            {
                return;
            }

            _lastPosition = position;
            var settings = _settings();
            if (!settings.SalesTurnSoundEnabled || settings.SalesTurnSoundVolume <= 0)
            {
                return;
            }

            if (position == PersonalSalesPosition.Current && settings.NotifySalesCurrent)
            {
                _sound.Play(SalesTurnNotificationKind.Current, settings.SalesTurnSoundVolume);
                _logger.Information("SALES-SOUND", "Current-turn notification requested.");
            }
            else if (position == PersonalSalesPosition.Next && settings.NotifySalesNext)
            {
                _sound.Play(SalesTurnNotificationKind.Next, settings.SalesTurnSoundVolume);
                _logger.Information("SALES-SOUND", "Next-turn notification requested.");
            }
        }
    }

    public void ResetBaseline()
    {
        lock (_sync)
        {
            _hasBaseline = false;
            _lastPosition = PersonalSalesPosition.Waiting;
        }
    }

    private enum PersonalSalesPosition
    {
        Waiting,
        Next,
        Current,
    }
}
