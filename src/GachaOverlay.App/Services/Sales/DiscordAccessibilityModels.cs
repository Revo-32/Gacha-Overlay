using GachaOverlay.Core.Sales;

namespace GachaOverlay.App.Services.Sales;

internal enum DiscordMessageContextKind
{
    ReactionGroup,
    MessageContent,
    MessageAccessories,
    ChatMessageContainer,
}

internal sealed record DiscordReactionGroupSnapshot(
    string MessageId,
    bool TraversalComplete,
    bool HasCompletionReaction);

internal sealed record DiscordMessageAccessibilityContext(
    string MessageId,
    DiscordMessageContextKind Kind,
    bool TraversalComplete,
    IReadOnlyList<DiscordReactionGroupSnapshot> ReactionGroups);

internal sealed record DiscordAccessibilitySnapshot(
    long WindowHandle,
    int ProcessId,
    string WindowTitle,
    bool WindowAvailable,
    bool AccessibilityReady,
    SalesTargetChannelStatus TargetChannelStatus,
    SalesChannelEvidenceSource ChannelEvidenceSource,
    SalesObservationReason FailureReason,
    bool TraversalComplete,
    IReadOnlyList<DiscordMessageAccessibilityContext> MessageContexts,
    int ScannedNodeCount,
    int ReactionGroupCount,
    bool WindowChanged,
    int WindowReacquisitionCount,
    int UiaExceptionCount,
    long ScanDurationMilliseconds)
{
    public static DiscordAccessibilitySnapshot Unavailable(
        SalesObservationReason reason,
        long durationMilliseconds = 0,
        int exceptionCount = 0) => new(
        0,
        0,
        string.Empty,
        false,
        false,
        SalesTargetChannelStatus.Unknown,
        SalesChannelEvidenceSource.None,
        reason,
        false,
        Array.Empty<DiscordMessageAccessibilityContext>(),
        0,
        0,
        false,
        0,
        exceptionCount,
        durationMilliseconds);
}

internal sealed record DiscordAccessibilityScanRequest(
    long SessionGeneration,
    long ScanGeneration,
    SalesObservationTargetSet TargetSet,
    bool FullResyncRequested);

internal interface IDiscordAccessibilityAdapter : IDisposable
{
    DiscordAccessibilitySnapshot Scan(
        DiscordAccessibilityScanRequest request,
        CancellationToken cancellationToken);

    void ResetSession();
}

internal static class DiscordSalesObservationInterpreter
{
    public static SalesObservationBatch Interpret(
        DiscordAccessibilitySnapshot snapshot,
        SalesObservationTargetSet targetSet,
        long generation,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targetSet);

        if (!snapshot.WindowAvailable || !snapshot.AccessibilityReady ||
            snapshot.TargetChannelStatus != SalesTargetChannelStatus.Selected)
        {
            return CreateUntrustedStatusBatch(snapshot, targetSet, generation, observedAt);
        }

        var contexts = snapshot.MessageContexts
            .GroupBy(context => context.MessageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var observations = new List<SaleReactionObservation>(targetSet.Targets.Count);
        var sold = 0;
        var notSold = 0;
        var notObserved = 0;
        foreach (var target in targetSet.Targets)
        {
            var outcome = ResolveOutcome(contexts.GetValueOrDefault(target.MessageId));
            var trusted = outcome != SaleReactionOutcome.NotObserved;
            observations.Add(new SaleReactionObservation(
                target.MessageId,
                outcome,
                trusted,
                observedAt,
                generation,
                target.SourceRevision));
            switch (outcome)
            {
                case SaleReactionOutcome.Sold:
                    sold++;
                    break;
                case SaleReactionOutcome.NotSold:
                    notSold++;
                    break;
                default:
                    notObserved++;
                    break;
            }
        }

        var observed = sold + notSold;
        var complete = snapshot.TraversalComplete && observed == targetSet.Targets.Count;
        var coverage = complete
            ? SalesCoverageState.Complete
            : observed > 0
                ? SalesCoverageState.Partial
                : targetSet.Targets.Count == 0 && snapshot.TraversalComplete
                    ? SalesCoverageState.Complete
                    : SalesCoverageState.None;
        var status = coverage == SalesCoverageState.Complete
            ? SalesObservationStatus.Live
            : SalesObservationStatus.Partial;
        return new SalesObservationBatch(
            generation,
            observedAt,
            status,
            true,
            coverage == SalesCoverageState.Complete
                ? SalesObservationCompleteness.Full
                : SalesObservationCompleteness.Partial,
            observations,
            coverage,
            coverage == SalesCoverageState.Complete
                ? SalesObservationReason.None
                : SalesObservationReason.CoverageIncomplete,
            targetSet.Targets.Count,
            observed,
            sold,
            notSold,
            notObserved,
            targetSet.Revision);
    }

    private static SaleReactionOutcome ResolveOutcome(
        IReadOnlyList<DiscordMessageAccessibilityContext>? contexts)
    {
        if (contexts is null || contexts.Count == 0)
        {
            return SaleReactionOutcome.NotObserved;
        }

        if (contexts.SelectMany(context => context.ReactionGroups)
            .Any(group => group.HasCompletionReaction))
        {
            return SaleReactionOutcome.Sold;
        }

        var complete = contexts.All(context =>
            context.TraversalComplete &&
            context.ReactionGroups.All(group => group.TraversalComplete));
        return complete
            ? SaleReactionOutcome.NotSold
            : SaleReactionOutcome.NotObserved;
    }

    private static SalesObservationBatch CreateUntrustedStatusBatch(
        DiscordAccessibilitySnapshot snapshot,
        SalesObservationTargetSet targetSet,
        long generation,
        DateTimeOffset observedAt)
    {
        var status = !snapshot.WindowAvailable
            ? SalesObservationStatus.Unavailable
            : !snapshot.AccessibilityReady
                ? SalesObservationStatus.AccessibilityUnavailable
                : snapshot.TargetChannelStatus is
                    SalesTargetChannelStatus.NotSelected or SalesTargetChannelStatus.Unknown
                    ? SalesObservationStatus.Paused
                    : SalesObservationStatus.Error;
        var reason = snapshot.FailureReason != SalesObservationReason.None
            ? snapshot.FailureReason
            : snapshot.TargetChannelStatus == SalesTargetChannelStatus.NotSelected
                ? SalesObservationReason.TargetChannelNotSelected
                : snapshot.TargetChannelStatus == SalesTargetChannelStatus.Unknown
                    ? SalesObservationReason.TargetChannelUnknown
                    : SalesObservationReason.ScanFailed;
        return new SalesObservationBatch(
            generation,
            observedAt,
            status,
            false,
            SalesObservationCompleteness.Partial,
            Array.Empty<SaleReactionObservation>(),
            SalesCoverageState.None,
            reason,
            targetSet.Targets.Count,
            0,
            0,
            0,
            targetSet.Targets.Count,
            targetSet.Revision);
    }
}
