using System.Collections.Concurrent;
using GachaOverlay.App.Services.Sales;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.Uia;

internal static class UiaSalesTestFactory
{
    public static SalesObservationTargetSet Targets(
        long revision = 1,
        params string[] messageIds) => new(
            revision,
            1,
            true,
            "1450076815581380730",
            "🚒판매모집",
            messageIds.Select((messageId, index) =>
                new SalesObservationTarget(messageId, index + 1)).ToArray());

    public static DiscordMessageAccessibilityContext Context(
        string messageId,
        bool complete = true,
        DiscordMessageContextKind kind = DiscordMessageContextKind.ChatMessageContainer,
        params DiscordReactionGroupSnapshot[] groups) => new(
            messageId,
            kind,
            complete,
            groups);

    public static DiscordReactionGroupSnapshot Group(
        string messageId,
        bool complete = true,
        bool hasCompletionReaction = false) => new(
        messageId,
        complete,
        hasCompletionReaction);

    public static DiscordAccessibilitySnapshot Selected(
        IReadOnlyList<DiscordMessageAccessibilityContext>? contexts = null,
        bool traversalComplete = true,
        long windowHandle = 100,
        bool windowChanged = false) => new(
            windowHandle,
            10,
            "#🚒판매모집 | Guild - Discord",
            true,
            true,
            SalesTargetChannelStatus.Selected,
            SalesChannelEvidenceSource.WindowTitleExact,
            SalesObservationReason.None,
            traversalComplete,
            contexts ?? Array.Empty<DiscordMessageAccessibilityContext>(),
            100,
            contexts?.Sum(context => context.ReactionGroups.Count) ?? 0,
            windowChanged,
            1,
            0,
            5);

    public static DiscordAccessibilitySnapshot Unavailable(
        SalesObservationReason reason = SalesObservationReason.DiscordNotRunning) =>
        DiscordAccessibilitySnapshot.Unavailable(reason);
}

internal sealed class ScriptedAccessibilityAdapter : IDiscordAccessibilityAdapter
{
    private readonly ConcurrentQueue<Func<DiscordAccessibilityScanRequest,
        CancellationToken, DiscordAccessibilitySnapshot>> _responses = new();
    private int _activeScans;
    private int _maximumConcurrentScans;
    private bool _disposed;

    public Func<DiscordAccessibilityScanRequest, CancellationToken,
        DiscordAccessibilitySnapshot>? DefaultResponse
    { get; set; }

    public int ScanCount { get; private set; }

    public int ResetCount { get; private set; }

    public int DisposeCount { get; private set; }

    public int MaximumConcurrentScans => _maximumConcurrentScans;

    public List<DiscordAccessibilityScanRequest> Requests { get; } = new();

    public void Enqueue(DiscordAccessibilitySnapshot response) =>
        _responses.Enqueue((_, _) => response);

    public void Enqueue(Func<DiscordAccessibilityScanRequest,
        CancellationToken, DiscordAccessibilitySnapshot> response) =>
        _responses.Enqueue(response);

    public DiscordAccessibilitySnapshot Scan(
        DiscordAccessibilityScanRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var active = Interlocked.Increment(ref _activeScans);
        SetMaximum(active);
        try
        {
            ScanCount++;
            lock (Requests)
            {
                Requests.Add(request);
            }

            if (_responses.TryDequeue(out var response))
            {
                return response(request, cancellationToken);
            }

            return DefaultResponse?.Invoke(request, cancellationToken) ??
                UiaSalesTestFactory.Unavailable();
        }
        finally
        {
            Interlocked.Decrement(ref _activeScans);
        }
    }

    public void ResetSession() => ResetCount++;

    public void Dispose()
    {
        _disposed = true;
        DisposeCount++;
    }

    private void SetMaximum(int value)
    {
        while (true)
        {
            var current = _maximumConcurrentScans;
            if (value <= current ||
                Interlocked.CompareExchange(
                    ref _maximumConcurrentScans,
                    value,
                    current) == current)
            {
                return;
            }
        }
    }
}
