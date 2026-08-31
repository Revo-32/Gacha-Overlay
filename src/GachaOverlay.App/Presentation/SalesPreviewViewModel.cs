using System.ComponentModel;
using System.Runtime.CompilerServices;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal sealed class SalesPreviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private IReadOnlyList<SalesPreviewScenarioOption> _scenarios;
    private SalesPreviewScenario _selectedScenario;
    private bool _disposed;

    public SalesPreviewViewModel(ILocalizationService localization, AppSettings settings)
    {
        Localization = localization;
        _settings = settings with { SalesTrackingEnabled = true };
        Sales = new SalesQueueViewModel(localization);
        _scenarios = CreateScenarioOptions();
        SelectedScenario = SalesPreviewScenario.Normal;
        Localization.LanguageChanged += OnLanguageChanged;
        ApplyScenario();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ILocalizationService Localization { get; }

    public SalesQueueViewModel Sales { get; }

    public IReadOnlyList<SalesPreviewScenarioOption> Scenarios
    {
        get => _scenarios;
        private set
        {
            _scenarios = value;
            OnPropertyChanged();
        }
    }

    public SalesPreviewScenario SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (_selectedScenario == value)
            {
                return;
            }

            _selectedScenario = value;
            OnPropertyChanged();
            ApplyScenario();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Localization.LanguageChanged -= OnLanguageChanged;
    }

    private void ApplyScenario()
    {
        var entries = CreateEntries(SelectedScenario);
        var status = SelectedScenario switch
        {
            SalesPreviewScenario.Paused => SalesObservationStatus.Paused,
            SalesPreviewScenario.Resyncing => SalesObservationStatus.Resyncing,
            SalesPreviewScenario.Degraded => SalesObservationStatus.Partial,
            SalesPreviewScenario.Error => SalesObservationStatus.Error,
            _ => SalesObservationStatus.Live,
        };
        var healthState = SelectedScenario switch
        {
            SalesPreviewScenario.Paused => SalesFeatureHealthState.Paused,
            SalesPreviewScenario.Resyncing => SalesFeatureHealthState.Resyncing,
            SalesPreviewScenario.Degraded => SalesFeatureHealthState.Degraded,
            SalesPreviewScenario.Disconnected => SalesFeatureHealthState.Disconnected,
            SalesPreviewScenario.Error => SalesFeatureHealthState.Error,
            _ => SalesFeatureHealthState.Live,
        };
        var snapshot = CreateSnapshot(entries, status, SelectedScenario);
        var health = new SalesFeatureHealthSnapshot(
            healthState,
            healthState switch
            {
                SalesFeatureHealthState.Paused => SalesFeatureHealthReason.TargetChannelNotSelected,
                SalesFeatureHealthState.Resyncing => SalesFeatureHealthReason.ResyncInProgress,
                SalesFeatureHealthState.Degraded => SalesFeatureHealthReason.CoveragePartial,
                SalesFeatureHealthState.Disconnected => SalesFeatureHealthReason.DiscordDisconnected,
                SalesFeatureHealthState.Error => SalesFeatureHealthReason.SensorFailure,
                _ => SalesFeatureHealthReason.None,
            },
            SalesObservationReason.None,
            status,
            healthState == SalesFeatureHealthState.Live
                ? SalesCoverageState.Complete
                : healthState == SalesFeatureHealthState.Degraded
                    ? SalesCoverageState.Partial
                    : SalesCoverageState.None,
            healthState == SalesFeatureHealthState.Live,
            healthState == SalesFeatureHealthState.Live ? DateTimeOffset.UtcNow : null,
            entries.Count,
            healthState == SalesFeatureHealthState.Live ? entries.Count : Math.Max(0, entries.Count - 1));
        Sales.UpdateHudContext(
            true,
            SelectedScenario == SalesPreviewScenario.UltraCompact,
            true,
            true);
        Sales.Apply(snapshot, _settings, health, "#sales", SalesQueueChangeContext.None);
        if (SelectedScenario == SalesPreviewScenario.SoldFade && entries.Count > 1)
        {
            var after = CreateSnapshot(entries.Skip(1).ToArray(), status, SelectedScenario);
            Sales.Apply(
                after,
                _settings,
                health,
                "#sales",
                new SalesQueueChangeContext(
                    true,
                    entries[0].MessageId,
                    entries[1].MessageId,
                    SalesQueueChangeReason.TrustedSold,
                    after.Revision));
        }
    }

    private static IReadOnlyList<SalesQueueEntry> CreateEntries(SalesPreviewScenario scenario)
    {
        if (scenario == SalesPreviewScenario.Empty)
        {
            return Array.Empty<SalesQueueEntry>();
        }

        var count = scenario switch
        {
            SalesPreviewScenario.OneItem => 1,
            SalesPreviewScenario.TwoItems => 2,
            _ => 4,
        };
        return Enumerable.Range(1, count)
            .Select(index => new SalesQueueEntry(
                $"preview-{index}",
                "preview-guild",
                $"user-{index}",
                DateTimeOffset.UtcNow.AddMinutes(index),
                scenario == SalesPreviewScenario.LongNames
                    ? $"Very long exact guild nickname for layout verification number {index}"
                    : index switch { 1 => "ItoToko", 2 => "Mina", 3 => "Ryu", _ => "Sora" },
                DiscordDisplayNameSource.GuildNickname,
                true,
                new SaleProduct(
                    $"product-{index}",
                    scenario == SalesPreviewScenario.LongNames
                        ? "Extremely Long Localized Product Display Name"
                        : $"Gacha {index}",
                    $"emoji-{index}",
                    $"gacha_{index}"),
                SaleObservationTrust.Trusted))
            .ToArray();
    }

    private static SalesQueueSnapshot CreateSnapshot(
        IReadOnlyList<SalesQueueEntry> entries,
        SalesObservationStatus status,
        SalesPreviewScenario scenario) => new(
            DateTimeOffset.UtcNow.Ticks,
            true,
            entries,
            entries.FirstOrDefault(),
            entries.Count,
            Math.Max(0, entries.Count - 1),
            entries.Skip(1).FirstOrDefault(),
            scenario == SalesPreviewScenario.CurrentTurn,
            scenario == SalesPreviewScenario.NextTurn,
            false,
            true,
            status,
            DateTimeOffset.UtcNow);

    private string ToDisplayName(SalesPreviewScenario value) => Localization[
        value switch
        {
            SalesPreviewScenario.Normal => "SalesPreviewNormal",
            SalesPreviewScenario.Empty => "SalesPreviewEmpty",
            SalesPreviewScenario.NextTurn => "SalesPreviewNextTurn",
            SalesPreviewScenario.CurrentTurn => "SalesPreviewCurrentTurn",
            SalesPreviewScenario.Paused => "SalesPreviewPaused",
            SalesPreviewScenario.Resyncing => "SalesPreviewResyncing",
            SalesPreviewScenario.Degraded => "SalesPreviewDegraded",
            SalesPreviewScenario.Disconnected => "SalesPreviewDisconnected",
            SalesPreviewScenario.Error => "SalesPreviewError",
            SalesPreviewScenario.SoldFade => "SalesPreviewSoldFade",
            SalesPreviewScenario.OneItem => "SalesPreviewOneItem",
            SalesPreviewScenario.TwoItems => "SalesPreviewTwoItems",
            SalesPreviewScenario.UltraCompact => "SalesPreviewUltraCompact",
            SalesPreviewScenario.LongNames => "SalesPreviewLongNames",
            _ => "SalesPreviewNormal",
        }];

    private IReadOnlyList<SalesPreviewScenarioOption> CreateScenarioOptions() =>
        Enum.GetValues<SalesPreviewScenario>()
            .Select(value => new SalesPreviewScenarioOption(value, ToDisplayName(value)))
            .ToArray();

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        Scenarios = CreateScenarioOptions();
        ApplyScenario();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal enum SalesPreviewScenario
{
    Normal,
    Empty,
    NextTurn,
    CurrentTurn,
    Paused,
    Resyncing,
    Degraded,
    Disconnected,
    Error,
    SoldFade,
    OneItem,
    TwoItems,
    UltraCompact,
    LongNames,
}

internal sealed record SalesPreviewScenarioOption(
    SalesPreviewScenario Value,
    string DisplayText)
{
    public override string ToString() => DisplayText;
}
