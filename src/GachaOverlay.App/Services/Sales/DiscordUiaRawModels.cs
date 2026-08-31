using GachaOverlay.Core.Sales;

namespace GachaOverlay.App.Services.Sales;

internal sealed record DiscordAccessibilityNodeInfo(
    string AutomationId,
    string Name,
    string ControlType,
    bool? IsSelected,
    bool IsOffscreen);

internal sealed record DiscordWindowCandidate(
    int ProcessId,
    long WindowHandle,
    string ProcessName,
    string WindowTitle,
    string WindowClassName,
    bool IsVisible);

internal static class DiscordWindowSelectionPolicy
{
    private static readonly HashSet<string> SupportedProcessNames = new(
        new[] { "Discord", "DiscordPTB", "DiscordCanary" },
        StringComparer.OrdinalIgnoreCase);

    public static DiscordWindowCandidate? Select(
        IEnumerable<DiscordWindowCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(IsEligible)
            .OrderByDescending(Score)
            .ThenBy(candidate => candidate.ProcessId)
            .ThenBy(candidate => candidate.WindowHandle)
            .FirstOrDefault();
    }

    private static bool IsEligible(DiscordWindowCandidate candidate) =>
        candidate.WindowHandle != 0 &&
        candidate.IsVisible &&
        SupportedProcessNames.Contains(candidate.ProcessName) &&
        candidate.WindowClassName.Equals("Chrome_WidgetWin_1", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(candidate.WindowTitle);

    private static int Score(DiscordWindowCandidate candidate)
    {
        var score = 10;
        if (candidate.WindowTitle.EndsWith(" - Discord", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (candidate.WindowTitle.Contains(" | ", StringComparison.Ordinal))
        {
            score += 20;
        }

        return score;
    }
}

internal static class DiscordAccessibilityAutomationIdParser
{
    public const string ReactionGroupPrefix = "message-reactions-";

    private const string MessageContentPrefix = "message-content-";
    private const string MessageAccessoriesPrefix = "message-accessories-";
    private const string ChatMessagePrefix = "chat-messages-";

    public static bool TryParseReactionGroupMessageId(
        string? automationId,
        out string messageId)
    {
        messageId = string.Empty;
        if (string.IsNullOrWhiteSpace(automationId) ||
            !automationId.StartsWith(ReactionGroupPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return TryNormalizeSnowflake(automationId[ReactionGroupPrefix.Length..], out messageId);
    }

    public static bool TryParseMessageContext(
        string? automationId,
        string targetChannelId,
        out string messageId,
        out DiscordMessageContextKind kind)
    {
        messageId = string.Empty;
        kind = default;
        if (TryParseReactionGroupMessageId(automationId, out messageId))
        {
            kind = DiscordMessageContextKind.ReactionGroup;
            return true;
        }

        if (TryParseSuffix(automationId, MessageContentPrefix, out messageId))
        {
            kind = DiscordMessageContextKind.MessageContent;
            return true;
        }

        if (TryParseSuffix(automationId, MessageAccessoriesPrefix, out messageId))
        {
            kind = DiscordMessageContextKind.MessageAccessories;
            return true;
        }

        if (string.IsNullOrWhiteSpace(automationId) ||
            string.IsNullOrWhiteSpace(targetChannelId) ||
            !automationId.StartsWith(ChatMessagePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = automationId[ChatMessagePrefix.Length..];
        var separator = suffix.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 || separator == suffix.Length - 1 ||
            !string.Equals(suffix[..separator], targetChannelId, StringComparison.Ordinal) ||
            !TryNormalizeSnowflake(suffix[(separator + 1)..], out messageId))
        {
            messageId = string.Empty;
            return false;
        }

        kind = DiscordMessageContextKind.ChatMessageContainer;
        return true;
    }

    public static bool IsTargetChannelMessageContainer(
        string? automationId,
        string targetChannelId) =>
        TryParseMessageContext(
            automationId,
            targetChannelId,
            out _,
            out var kind) &&
        kind == DiscordMessageContextKind.ChatMessageContainer;

    private static bool TryParseSuffix(
        string? automationId,
        string prefix,
        out string messageId)
    {
        messageId = string.Empty;
        return !string.IsNullOrWhiteSpace(automationId) &&
            automationId.StartsWith(prefix, StringComparison.Ordinal) &&
            TryNormalizeSnowflake(automationId[prefix.Length..], out messageId);
    }

    private static bool TryNormalizeSnowflake(string value, out string snowflake)
    {
        snowflake = string.Empty;
        if (value.Length == 0 ||
            value.Any(character => !char.IsAsciiDigit(character)) ||
            !ulong.TryParse(value, out var numeric) ||
            numeric == 0)
        {
            return false;
        }

        snowflake = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}

internal static class DiscordTargetChannelDetector
{
    private const string ChannelItemPrefix = "channels___";

    public static (SalesTargetChannelStatus Status, SalesChannelEvidenceSource Evidence) Detect(
        string windowTitle,
        string targetChannelId,
        string targetChannelName,
        IReadOnlyCollection<DiscordAccessibilityNodeInfo> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var normalizedTargetName = NormalizeChannelName(targetChannelName);
        var titleChannel = ParseWindowTitleChannel(windowTitle);
        if (!string.IsNullOrWhiteSpace(titleChannel))
        {
            return string.Equals(
                titleChannel,
                normalizedTargetName,
                StringComparison.Ordinal)
                    ? (SalesTargetChannelStatus.Selected,
                        SalesChannelEvidenceSource.WindowTitleExact)
                    : (SalesTargetChannelStatus.NotSelected,
                        SalesChannelEvidenceSource.WindowTitleExact);
        }

        if (!string.IsNullOrWhiteSpace(targetChannelId))
        {
            var exactTargetItemId = ChannelItemPrefix + targetChannelId;
            if (nodes.Any(node =>
                    node.IsSelected == true &&
                    string.Equals(node.AutomationId, exactTargetItemId, StringComparison.Ordinal)))
            {
                return (
                    SalesTargetChannelStatus.Selected,
                    SalesChannelEvidenceSource.ChannelIdAnchor);
            }

            if (nodes.Any(node =>
                    node.IsSelected == true &&
                    node.AutomationId.StartsWith(ChannelItemPrefix, StringComparison.Ordinal) &&
                    !string.Equals(node.AutomationId, exactTargetItemId, StringComparison.Ordinal)))
            {
                return (
                    SalesTargetChannelStatus.NotSelected,
                    SalesChannelEvidenceSource.ChannelIdAnchor);
            }

            if (nodes.Any(node =>
                    DiscordAccessibilityAutomationIdParser.IsTargetChannelMessageContainer(
                        node.AutomationId,
                        targetChannelId)))
            {
                return (
                    SalesTargetChannelStatus.Selected,
                    SalesChannelEvidenceSource.MessageContainerChannelId);
            }
        }

        var selectedChannelItems = nodes
            .Where(node => node.IsSelected == true)
            .Select(node => NormalizeChannelName(node.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedChannelItems.Contains(normalizedTargetName, StringComparer.Ordinal))
        {
            return (
                SalesTargetChannelStatus.Selected,
                SalesChannelEvidenceSource.SelectedChannelItem);
        }

        if (selectedChannelItems.Length > 0)
        {
            return (
                SalesTargetChannelStatus.NotSelected,
                SalesChannelEvidenceSource.SelectedChannelItem);
        }

        return (SalesTargetChannelStatus.Unknown, SalesChannelEvidenceSource.None);
    }

    private static string ParseWindowTitleChannel(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var separator = title.IndexOf(" | ", StringComparison.Ordinal);
        if (separator <= 0 ||
            !title.EndsWith(" - Discord", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return NormalizeChannelName(title[..separator]);
    }

    private static string NormalizeChannelName(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith('#') ? normalized[1..].Trim() : normalized;
    }
}
