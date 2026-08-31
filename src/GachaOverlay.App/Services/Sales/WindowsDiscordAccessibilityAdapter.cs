using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.App.Services.Sales;

internal sealed class WindowsDiscordAccessibilityAdapter : IDiscordAccessibilityAdapter
{
    internal const int MaximumCachedNodeCount = 8000;

    private readonly IDiscordWindowLocator _windowLocator;
    private long _lastWindowHandle;
    private int _lastProcessId;
    private bool? _lastAccessibilityReady;
    private int _windowReacquisitionCount;
    private int _uiaExceptionCount;
    private bool _disposed;

    public WindowsDiscordAccessibilityAdapter(IDiscordWindowLocator? windowLocator = null)
    {
        _windowLocator = windowLocator ?? new Win32DiscordWindowLocator();
    }

    public DiscordAccessibilitySnapshot Scan(
        DiscordAccessibilityScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var window = _windowLocator.Locate();
            if (window is null)
            {
                ResetWindowIdentity();
                return DiscordAccessibilitySnapshot.Unavailable(
                    IsDiscordProcessRunning()
                        ? SalesObservationReason.DiscordWindowNotFound
                        : SalesObservationReason.DiscordNotRunning,
                    stopwatch.ElapsedMilliseconds,
                    _uiaExceptionCount);
            }

            var windowChanged = window.WindowHandle != _lastWindowHandle ||
                window.ProcessId != _lastProcessId;
            if (windowChanged)
            {
                _lastWindowHandle = window.WindowHandle;
                _lastProcessId = window.ProcessId;
                _lastAccessibilityReady = null;
                _windowReacquisitionCount++;
            }

            var quickChannel = DiscordTargetChannelDetector.Detect(
                window.WindowTitle,
                request.TargetSet.SalesChannelId,
                request.TargetSet.SalesChannelName,
                Array.Empty<DiscordAccessibilityNodeInfo>());
            if (_lastAccessibilityReady == true &&
                quickChannel.Status == SalesTargetChannelStatus.NotSelected)
            {
                return new DiscordAccessibilitySnapshot(
                    window.WindowHandle,
                    window.ProcessId,
                    window.WindowTitle,
                    true,
                    true,
                    quickChannel.Status,
                    quickChannel.Evidence,
                    SalesObservationReason.TargetChannelNotSelected,
                    false,
                    Array.Empty<DiscordMessageAccessibilityContext>(),
                    0,
                    0,
                    windowChanged,
                    _windowReacquisitionCount,
                    _uiaExceptionCount,
                    stopwatch.ElapsedMilliseconds);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var root = AutomationElement.FromHandle(new IntPtr(window.WindowHandle));
            if (root is null)
            {
                _lastAccessibilityReady = false;
                return CreateFailure(
                    window,
                    windowChanged,
                    SalesObservationReason.AccessibilityTreeUnavailable,
                    stopwatch.ElapsedMilliseconds);
            }

            var cache = CreateCacheRequest();
            AutomationElement cachedRoot;
            using (cache.Activate())
            {
                cachedRoot = root.GetUpdatedCache(cache);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var captured = CaptureCachedTree(
                cachedRoot,
                request.TargetSet.SalesChannelId,
                cancellationToken);
            var accessibilityReady = captured.Nodes.Count >= 16 &&
                captured.Nodes.Any(IsMeaningfulAccessibilityAnchor);
            _lastAccessibilityReady = accessibilityReady;
            var channel = DiscordTargetChannelDetector.Detect(
                window.WindowTitle,
                request.TargetSet.SalesChannelId,
                request.TargetSet.SalesChannelName,
                captured.Nodes);
            var reason = !accessibilityReady
                ? SalesObservationReason.AccessibilityTreeUnavailable
                : channel.Status == SalesTargetChannelStatus.NotSelected
                    ? SalesObservationReason.TargetChannelNotSelected
                    : channel.Status == SalesTargetChannelStatus.Unknown
                        ? SalesObservationReason.TargetChannelUnknown
                        : captured.TraversalComplete
                            ? SalesObservationReason.None
                            : SalesObservationReason.ScanFailed;

            return new DiscordAccessibilitySnapshot(
                window.WindowHandle,
                window.ProcessId,
                window.WindowTitle,
                true,
                accessibilityReady,
                channel.Status,
                channel.Evidence,
                reason,
                captured.TraversalComplete,
                captured.Contexts,
                captured.Nodes.Count,
                captured.ReactionGroupCount,
                windowChanged,
                _windowReacquisitionCount,
                _uiaExceptionCount,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ElementNotAvailableException)
        {
            return RecoverFromUiaFailure(
                SalesObservationReason.ElementUnavailable,
                stopwatch.ElapsedMilliseconds);
        }
        catch (COMException)
        {
            return RecoverFromUiaFailure(
                SalesObservationReason.ElementUnavailable,
                stopwatch.ElapsedMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return RecoverFromUiaFailure(
                SalesObservationReason.ScanFailed,
                stopwatch.ElapsedMilliseconds);
        }
    }

    public void ResetSession()
    {
        ResetWindowIdentity();
        _lastAccessibilityReady = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ResetSession();
        _disposed = true;
    }

    private static CacheRequest CreateCacheRequest()
    {
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = Automation.RawViewCondition,
            TreeScope = TreeScope.Element | TreeScope.Descendants,
        };
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(SelectionItemPattern.IsSelectedProperty);
        return cache;
    }

    private static CapturedTree CaptureCachedTree(
        AutomationElement cachedRoot,
        string targetChannelId,
        CancellationToken cancellationToken)
    {
        var nodes = new List<DiscordAccessibilityNodeInfo>();
        var contexts = new Dictionary<string, ContextBuilder>(StringComparer.Ordinal);
        var stack = new Stack<TraversalItem>();
        stack.Push(new TraversalItem(cachedRoot, null, null));
        var complete = true;
        var reactionGroupCount = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.Count >= MaximumCachedNodeCount)
            {
                complete = false;
                break;
            }

            var item = stack.Pop();
            var automationId = ReadCachedString(
                item.Element,
                AutomationElement.AutomationIdProperty);
            var name = ReadCachedString(item.Element, AutomationElement.NameProperty);
            var controlType = ReadCachedControlType(item.Element);
            var selected = ReadCachedNullableBoolean(
                item.Element,
                SelectionItemPattern.IsSelectedProperty);
            var offscreen = ReadCachedBoolean(
                item.Element,
                AutomationElement.IsOffscreenProperty);
            nodes.Add(new DiscordAccessibilityNodeInfo(
                automationId,
                name,
                controlType,
                selected,
                offscreen));

            var currentContext = item.Context;
            var currentReaction = item.ReactionGroup;
            if (DiscordAccessibilityAutomationIdParser.TryParseMessageContext(
                    automationId,
                    targetChannelId,
                    out var messageId,
                    out var kind))
            {
                currentContext = GetOrCreateContext(contexts, messageId, kind);
                currentReaction = null;
                if (kind == DiscordMessageContextKind.ReactionGroup)
                {
                    currentReaction = new ReactionGroupBuilder(messageId);
                    currentContext.ReactionGroups.Add(currentReaction);
                    reactionGroupCount++;
                }
            }

            if (currentReaction is not null && !string.IsNullOrWhiteSpace(name))
            {
                currentReaction.ObserveAccessibleName(name);
            }

            try
            {
                var children = item.Element.CachedChildren;
                for (var index = children.Count - 1; index >= 0; index--)
                {
                    stack.Push(new TraversalItem(
                        children[index],
                        currentContext,
                        currentReaction));
                }
            }
            catch (InvalidOperationException)
            {
                complete = false;
                if (currentContext is not null)
                {
                    currentContext.TraversalComplete = false;
                }

                if (currentReaction is not null)
                {
                    currentReaction.TraversalComplete = false;
                }
            }
        }

        if (!complete)
        {
            foreach (var context in contexts.Values)
            {
                context.TraversalComplete = false;
                foreach (var reaction in context.ReactionGroups)
                {
                    reaction.TraversalComplete = false;
                }
            }
        }

        return new CapturedTree(
            nodes,
            contexts.Values
                .Select(context => context.ToSnapshot())
                .ToArray(),
            complete,
            reactionGroupCount);
    }

    private DiscordAccessibilitySnapshot RecoverFromUiaFailure(
        SalesObservationReason reason,
        long durationMilliseconds)
    {
        _uiaExceptionCount++;
        ResetWindowIdentity();
        _lastAccessibilityReady = false;
        return DiscordAccessibilitySnapshot.Unavailable(
            reason,
            durationMilliseconds,
            _uiaExceptionCount);
    }

    private DiscordAccessibilitySnapshot CreateFailure(
        DiscordWindowCandidate window,
        bool windowChanged,
        SalesObservationReason reason,
        long durationMilliseconds) => new(
            window.WindowHandle,
            window.ProcessId,
            window.WindowTitle,
            true,
            false,
            SalesTargetChannelStatus.Unknown,
            SalesChannelEvidenceSource.None,
            reason,
            false,
            Array.Empty<DiscordMessageAccessibilityContext>(),
            0,
            0,
            windowChanged,
            _windowReacquisitionCount,
            _uiaExceptionCount,
            durationMilliseconds);

    private static bool IsDiscordProcessRunning()
    {
        foreach (var processName in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    private void ResetWindowIdentity()
    {
        _lastWindowHandle = 0;
        _lastProcessId = 0;
    }

    private static ContextBuilder GetOrCreateContext(
        IDictionary<string, ContextBuilder> contexts,
        string messageId,
        DiscordMessageContextKind kind)
    {
        if (contexts.TryGetValue(messageId, out var existing))
        {
            existing.PromoteKind(kind);
            return existing;
        }

        var created = new ContextBuilder(messageId, kind);
        contexts.Add(messageId, created);
        return created;
    }

    private static string ReadCachedString(
        AutomationElement element,
        AutomationProperty property)
    {
        var value = element.GetCachedPropertyValue(property, true);
        return ReferenceEquals(value, AutomationElement.NotSupported)
            ? string.Empty
            : value as string ?? string.Empty;
    }

    private static string ReadCachedControlType(AutomationElement element)
    {
        var value = element.GetCachedPropertyValue(
            AutomationElement.ControlTypeProperty,
            true);
        return value is ControlType controlType
            ? controlType.ProgrammaticName
            : string.Empty;
    }

    private static bool ReadCachedBoolean(
        AutomationElement element,
        AutomationProperty property)
    {
        var value = element.GetCachedPropertyValue(property, true);
        return value is bool boolean && boolean;
    }

    private static bool? ReadCachedNullableBoolean(
        AutomationElement element,
        AutomationProperty property)
    {
        var value = element.GetCachedPropertyValue(property, true);
        return value is bool boolean ? boolean : null;
    }

    private static bool IsMeaningfulAccessibilityAnchor(DiscordAccessibilityNodeInfo node) =>
        node.AutomationId.StartsWith("channels___", StringComparison.Ordinal) ||
        node.AutomationId.StartsWith("chat-messages", StringComparison.Ordinal) ||
        node.AutomationId.StartsWith("message-", StringComparison.Ordinal) ||
        node.ControlType is "ControlType.Document" or "ControlType.List";

    private sealed record TraversalItem(
        AutomationElement Element,
        ContextBuilder? Context,
        ReactionGroupBuilder? ReactionGroup);

    private sealed record CapturedTree(
        IReadOnlyList<DiscordAccessibilityNodeInfo> Nodes,
        IReadOnlyList<DiscordMessageAccessibilityContext> Contexts,
        bool TraversalComplete,
        int ReactionGroupCount);

    private sealed class ContextBuilder
    {
        public ContextBuilder(string messageId, DiscordMessageContextKind kind)
        {
            MessageId = messageId;
            Kind = kind;
        }

        public string MessageId { get; }

        public DiscordMessageContextKind Kind { get; private set; }

        public bool TraversalComplete { get; set; } = true;

        public List<ReactionGroupBuilder> ReactionGroups { get; } = new();

        public void PromoteKind(DiscordMessageContextKind kind)
        {
            if (kind == DiscordMessageContextKind.ChatMessageContainer ||
                Kind == DiscordMessageContextKind.ReactionGroup)
            {
                Kind = kind;
            }
        }

        public DiscordMessageAccessibilityContext ToSnapshot() => new(
            MessageId,
            Kind,
            TraversalComplete,
            ReactionGroups.Select(reaction => reaction.ToSnapshot()).ToArray());
    }

    private sealed class ReactionGroupBuilder
    {
        public ReactionGroupBuilder(string messageId)
        {
            MessageId = messageId;
        }

        public string MessageId { get; }

        public bool TraversalComplete { get; set; } = true;

        public bool HasCompletionReaction { get; private set; }

        public void ObserveAccessibleName(string accessibleName)
        {
            if (DiscordSalesCompletionReactionMatcher
                .MatchAccessibleNameFallback(accessibleName)
                .IsCompletion)
            {
                HasCompletionReaction = true;
            }
        }

        public DiscordReactionGroupSnapshot ToSnapshot() => new(
            MessageId,
            TraversalComplete,
            HasCompletionReaction);
    }
}
