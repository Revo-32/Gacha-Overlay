using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.M7;

internal static class M7PresentationTestFactory
{
    public static SalesQueuePresentationStrings Strings { get; } = new(
        "Sales live",
        "Connecting to Discord",
        "Resyncing sales status",
        "Remote Sales connecting",
        "Remote Sales synchronizing",
        "Remote Sales resynchronizing",
        "Remote Sales reconnecting",
        "Remote Sales paused",
        "Partial sales status",
        "Discord disconnected",
        "Sales sensor unavailable",
        "Current {0}",
        "Waiting {0}",
        "Product {0}",
        "Next {0}",
        "No one waiting",
        "No fields",
        "I'm next",
        "Sell now");

    public static SalesQueuePresentationState Create(
        SalesQueueSnapshot? queue = null,
        SalesFeatureHealthSnapshot? health = null,
        SalesQueueDisplayOptions? options = null,
        SalesQueuePresentationState? previous = null,
        SalesQueueChangeContext? change = null,
        double width = 500,
        SalesQueueFieldMeasurements? measurements = null,
        bool ultraCompact = false,
        bool hudVisible = true,
        bool animationsEnabled = true,
        string channel = "#sales") =>
        SalesQueuePresentationFactory.Create(new SalesQueuePresentationInput(
            queue ?? Queue(),
            health ?? Health(SalesFeatureHealthState.Live),
            options ?? new SalesQueueDisplayOptions(true, true, false, false),
            Strings,
            channel,
            width,
            measurements ?? new SalesQueueFieldMeasurements(100, 70, 90, 90),
            previous,
            change ?? SalesQueueChangeContext.None,
            ultraCompact,
            hudVisible,
            animationsEnabled));

    public static SalesQueueSnapshot Queue(
        bool tracking = true,
        bool empty = false,
        bool currentSelf = false,
        bool nextSelf = false,
        SaleObservationTrust currentTrust = SaleObservationTrust.Trusted,
        SaleObservationTrust nextTrust = SaleObservationTrust.Trusted,
        string currentId = "1",
        string nextId = "2",
        SaleProduct? product = null,
        int extraWaiting = 0,
        long revision = 10)
    {
        if (empty)
        {
            return new SalesQueueSnapshot(
                revision,
                tracking,
                Array.Empty<SalesQueueEntry>(),
                null,
                0,
                0,
                null,
                false,
                false,
                false,
                true,
                SalesObservationStatus.Live,
                SalesTestFactory.Epoch);
        }

        var current = Entry(
            currentId,
            currentSelf ? "self" : "seller",
            "Seller",
            currentTrust,
            product);
        var next = Entry(
            nextId,
            nextSelf ? "self" : "next",
            "NextUser",
            nextTrust);
        var active = new List<SalesQueueEntry> { current, next };
        for (var index = 0; index < extraWaiting; index++)
        {
            active.Add(Entry(
                $"extra-{index}",
                $"extra-author-{index}",
                $"Extra{index}",
                SaleObservationTrust.Trusted));
        }

        return new SalesQueueSnapshot(
            revision,
            tracking,
            active,
            current,
            active.Count,
            active.Count - 1,
            next,
            currentSelf,
            nextSelf,
            active.Any(item => item.IsProvisional),
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Epoch);
    }

    public static SalesQueueEntry Entry(
        string id,
        string authorId,
        string name,
        SaleObservationTrust trust,
        SaleProduct? product = null) => new(
            id,
            "guild",
            authorId,
            SalesTestFactory.Epoch,
            name,
            DiscordDisplayNameSource.GuildNickname,
            true,
            product,
            trust);

    public static SalesFeatureHealthSnapshot Health(
        SalesFeatureHealthState state,
        SalesFeatureHealthReason reason = SalesFeatureHealthReason.None,
        SalesCoverageState? coverage = null) => new(
            state,
            reason,
            SalesObservationReason.None,
            state switch
            {
                SalesFeatureHealthState.Live => SalesObservationStatus.Live,
                SalesFeatureHealthState.Paused => SalesObservationStatus.Paused,
                SalesFeatureHealthState.Resyncing => SalesObservationStatus.Resyncing,
                SalesFeatureHealthState.Degraded => SalesObservationStatus.Partial,
                SalesFeatureHealthState.Disabled => SalesObservationStatus.Disabled,
                SalesFeatureHealthState.Error => SalesObservationStatus.Error,
                _ => SalesObservationStatus.Unavailable,
            },
            coverage ?? (state == SalesFeatureHealthState.Live
                ? SalesCoverageState.Complete
                : state == SalesFeatureHealthState.Degraded
                    ? SalesCoverageState.Partial
                    : SalesCoverageState.None),
            state == SalesFeatureHealthState.Live,
            state == SalesFeatureHealthState.Live ? SalesTestFactory.Epoch : null,
            2,
            state == SalesFeatureHealthState.Live ? 2 : 0);

    public static SalesQueueChangeContext SoldChange(
        string previous = "1",
        string current = "2") => new(
            true,
            previous,
            current,
            SalesQueueChangeReason.TrustedSold,
            11);
}
