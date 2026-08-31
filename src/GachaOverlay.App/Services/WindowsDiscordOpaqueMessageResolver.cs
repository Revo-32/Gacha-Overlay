using System.Runtime.InteropServices;
using System.Windows.Automation;
using GachaOverlay.App.Services.Sales;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.App.Services;

internal sealed class WindowsDiscordOpaqueMessageResolver : IDiscordOpaqueMessageResolver
{
    private static readonly string[] ForwardMarkers =
    {
        "Forwarded",
        "전달됨",
        "転送済み",
        "転送されました",
    };

    private static readonly string[] StickerMarkers =
    {
        "Sticker",
        "스티커",
        "ステッカー",
    };

    private readonly IDiscordWindowLocator _windowLocator;

    public WindowsDiscordOpaqueMessageResolver(IDiscordWindowLocator? windowLocator = null)
    {
        _windowLocator = windowLocator ?? new Win32DiscordWindowLocator();
    }

    public Task<DiscordOpaqueMessageResolution> ResolveAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return Task.Run(
            () => Resolve(channelId, messageId, cancellationToken),
            cancellationToken);
    }

    private DiscordOpaqueMessageResolution Resolve(
        string channelId,
        string messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = _windowLocator.Locate();
            if (window is null)
            {
                return Unknown();
            }

            var root = AutomationElement.FromHandle(new IntPtr(window.WindowHandle));
            if (root is null)
            {
                return Unknown();
            }

            var container = FindByAutomationId(
                root,
                $"chat-messages-{channelId}-{messageId}");
            if (container is null)
            {
                return Unknown();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var descendants = container.FindAll(
                TreeScope.Descendants,
                Condition.TrueCondition);
            var observations = new List<UiObservation>(descendants.Count);
            for (var index = 0; index < descendants.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var element = descendants[index];
                var controlType = element.Current.ControlType;
                var name = element.Current.Name?.Trim() ?? string.Empty;
                observations.Add(new UiObservation(
                    controlType == ControlType.Text,
                    controlType == ControlType.Image,
                    name));
            }

            return Classify(observations);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or
            COMException or
            InvalidOperationException)
        {
            return Unknown();
        }
    }

    internal static DiscordOpaqueMessageResolution Classify(
        IReadOnlyList<UiObservation> observations)
    {
        var forwarded = false;
        var positiveStickerEvidence = false;
        var forwardedText = new List<string>();
        foreach (var observation in observations)
        {
            if (observation.IsText && IsMarker(observation.Name, ForwardMarkers))
            {
                forwarded = true;
                continue;
            }

            if (forwarded &&
                observation.IsText &&
                !string.IsNullOrWhiteSpace(observation.Name) &&
                (forwardedText.Count == 0 || !string.Equals(
                    forwardedText[^1],
                    observation.Name,
                    StringComparison.Ordinal)))
            {
                forwardedText.Add(observation.Name);
            }

            positiveStickerEvidence |= observation.IsImage &&
                IsMarker(observation.Name, StickerMarkers, exact: false);
        }

        if (forwarded)
        {
            if (forwardedText.Count > 0)
            {
                return new DiscordOpaqueMessageResolution(
                    DiscordOpaqueMessageResolutionKind.ForwardedText,
                    string.Join(' ', forwardedText));
            }

            return new DiscordOpaqueMessageResolution(
                DiscordOpaqueMessageResolutionKind.ForwardedMessage);
        }

        return positiveStickerEvidence
            ? new DiscordOpaqueMessageResolution(
                DiscordOpaqueMessageResolutionKind.Sticker)
            : Unknown();
    }

    private static AutomationElement? FindByAutomationId(
        AutomationElement root,
        string automationId) =>
        root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                automationId));

    private static bool IsMarker(
        string value,
        IEnumerable<string> markers,
        bool exact = true) => markers.Any(marker => exact
            ? string.Equals(marker, value, StringComparison.OrdinalIgnoreCase)
            : value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static DiscordOpaqueMessageResolution Unknown() => new(
        DiscordOpaqueMessageResolutionKind.Unknown);

    internal readonly record struct UiObservation(
        bool IsText,
        bool IsImage,
        string Name);
}
