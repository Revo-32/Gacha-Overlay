namespace GachaOverlay.Core.Sales;

public sealed record SalesHistoryEntry(string ProductId, DateTimeOffset LastSoldAt);

public interface ISalesHistoryStore
{
    event Action? Changed;

    IReadOnlyList<SalesHistoryEntry> Snapshot();

    bool RecordSold(IReadOnlyCollection<string> productIds, DateTimeOffset soldAt);

    bool Clear();
}

public sealed record SalesHistoryTransitionCandidate(
    string MessageId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> ProductIds);

public sealed class SalesHistoryTransitionRecorder
{
    private readonly ISalesHistoryStore _store;

    public SalesHistoryTransitionRecorder(ISalesHistoryStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<SalesHistoryTransitionCandidate> CapturePendingOwn(
        bool enabled,
        SalesObservationBatch batch,
        IReadOnlyCollection<SaleRecord> records,
        string? authenticatedUserId)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(records);
        if (!enabled ||
            !batch.IsTrusted ||
            batch.SensorStatus != SalesObservationStatus.Live ||
            batch.Completeness != SalesObservationCompleteness.Full ||
            string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            return Array.Empty<SalesHistoryTransitionCandidate>();
        }

        return batch.Observations
            .Where(observation =>
                observation.HasTrustedEvidence &&
                observation.Outcome == SaleReactionOutcome.Sold)
            .Join(
                records.Where(record =>
                    record.DomainState == SaleDomainState.Pending &&
                    record.ParseStatus is SaleParseStatus.Parsed or
                        SaleParseStatus.PartiallyParsed &&
                    record.AllProducts.Count > 0 &&
                    string.Equals(
                        record.AuthorId,
                        authenticatedUserId,
                        StringComparison.Ordinal)),
                observation => observation.MessageId,
                record => record.MessageId,
                (observation, record) => new SalesHistoryTransitionCandidate(
                    observation.MessageId,
                    observation.ObservedAt,
                    record.AllProducts
                        .Select(product => product.ProductId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()))
            .ToArray();
    }

    public void RecordConfirmedSold(
        IReadOnlyCollection<SalesHistoryTransitionCandidate> candidates,
        IReadOnlyCollection<SaleRecord> authoritativeReadback)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(authoritativeReadback);
        if (candidates.Count == 0)
        {
            return;
        }

        var soldIds = authoritativeReadback
            .Where(record => record.DomainState == SaleDomainState.Sold)
            .Select(record => record.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(candidate =>
                     soldIds.Contains(candidate.MessageId)))
        {
            _store.RecordSold(candidate.ProductIds, candidate.ObservedAt);
        }
    }
}
