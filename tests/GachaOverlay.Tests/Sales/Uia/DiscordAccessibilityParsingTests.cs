using GachaOverlay.App.Services.Sales;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.Uia;

public sealed class DiscordAccessibilityParsingTests
{
    [Theory]
    [InlineData("message-reactions-1543368353781907548", "1543368353781907548")]
    [InlineData("message-reactions-18446744073709551615", "18446744073709551615")]
    [InlineData("message-reactions-000123", "123")]
    public void ReactionGroupParser_PreservesNumericSnowflake(
        string automationId,
        string expected)
    {
        Assert.True(DiscordAccessibilityAutomationIdParser.TryParseReactionGroupMessageId(
            automationId,
            out var messageId));
        Assert.Equal(expected, messageId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("message-reaction-123")]
    [InlineData("message-reactions-")]
    [InlineData("message-reactions-abc")]
    [InlineData("message-reactions-0")]
    [InlineData("prefix-message-reactions-123")]
    [InlineData("message-reactions-123-extra")]
    [InlineData("message-content-123")]
    public void ReactionGroupParser_IgnoresMalformedOrUnrelatedId(string? automationId) =>
        Assert.False(DiscordAccessibilityAutomationIdParser.TryParseReactionGroupMessageId(
            automationId,
            out _));

    [Theory]
    [InlineData("message-content-123", "MessageContent", "123")]
    [InlineData("message-accessories-456", "MessageAccessories", "456")]
    [InlineData("message-reactions-789", "ReactionGroup", "789")]
    [InlineData("chat-messages-1450076815581380730-999", "ChatMessageContainer", "999")]
    public void MessageContextParser_RecognizesStrictDiscordPatterns(
        string automationId,
        string expectedKind,
        string expectedMessageId)
    {
        Assert.True(DiscordAccessibilityAutomationIdParser.TryParseMessageContext(
            automationId,
            "1450076815581380730",
            out var messageId,
            out var kind));
        Assert.Equal(expectedKind, kind.ToString());
        Assert.Equal(expectedMessageId, messageId);
    }

    [Theory]
    [InlineData("chat-messages-999-123")]
    [InlineData("chat-messages-1450076815581380730")]
    [InlineData("chat-messages-1450076815581380730-abc")]
    [InlineData("message-content-abc")]
    [InlineData("unrelated-123")]
    public void MessageContextParser_RejectsWrongChannelOrMalformedId(string automationId) =>
        Assert.False(DiscordAccessibilityAutomationIdParser.TryParseMessageContext(
            automationId,
            "1450076815581380730",
            out _,
            out _));

    [Fact]
    public void ChannelDetector_UsesAuditedExactWindowTitle()
    {
        var result = DiscordTargetChannelDetector.Detect(
            "#🚒판매모집 | GTA5 마이너 갤러리 - Discord",
            "1450076815581380730",
            "🚒판매모집",
            Array.Empty<DiscordAccessibilityNodeInfo>());
        Assert.Equal(SalesTargetChannelStatus.Selected, result.Status);
        Assert.Equal(SalesChannelEvidenceSource.WindowTitleExact, result.Evidence);
    }

    [Fact]
    public void ChannelDetector_ExactOtherWindowTitleIsNotSelected()
    {
        var result = DiscordTargetChannelDetector.Detect(
            "#🏠메인 | GTA5 마이너 갤러리 - Discord",
            "1450076815581380730",
            "🚒판매모집",
            Array.Empty<DiscordAccessibilityNodeInfo>());
        Assert.Equal(SalesTargetChannelStatus.NotSelected, result.Status);
    }

    [Fact]
    public void ChannelDetector_UsesSelectedChannelIdAnchor()
    {
        var nodes = new[]
        {
            Node("channels___1450076815581380730", "🚒판매모집", selected: true),
        };
        var result = Detect(nodes);
        Assert.Equal(SalesTargetChannelStatus.Selected, result.Status);
        Assert.Equal(SalesChannelEvidenceSource.ChannelIdAnchor, result.Evidence);
    }

    [Fact]
    public void ChannelDetector_SelectedDifferentChannelIdIsNotSelected()
    {
        var result = Detect(new[] { Node("channels___123", "🏠메인", selected: true) });
        Assert.Equal(SalesTargetChannelStatus.NotSelected, result.Status);
    }

    [Fact]
    public void ChannelDetector_UsesTargetChannelMessageContainer()
    {
        var result = Detect(new[]
        {
            Node("chat-messages-1450076815581380730-1543368353781907548", string.Empty),
        });
        Assert.Equal(SalesTargetChannelStatus.Selected, result.Status);
        Assert.Equal(SalesChannelEvidenceSource.MessageContainerChannelId, result.Evidence);
    }

    [Fact]
    public void ChannelDetector_ExactSelectedNameFallbackIsSupported()
    {
        var result = Detect(new[] { Node(string.Empty, "#🚒판매모집", selected: true) });
        Assert.Equal(SalesTargetChannelStatus.Selected, result.Status);
        Assert.Equal(SalesChannelEvidenceSource.SelectedChannelItem, result.Evidence);
    }

    [Fact]
    public void ChannelDetector_AmbiguousEvidenceIsUnknown()
    {
        var result = Detect(new[] { Node("message-reactions-123", "SOLD") });
        Assert.Equal(SalesTargetChannelStatus.Unknown, result.Status);
        Assert.Equal(SalesChannelEvidenceSource.None, result.Evidence);
    }

    [Fact]
    public void ChannelDetector_ZeroReactionNodesDoesNotMeanOtherChannel()
    {
        var result = Detect(Array.Empty<DiscordAccessibilityNodeInfo>());
        Assert.Equal(SalesTargetChannelStatus.Unknown, result.Status);
    }

    [Fact]
    public void ChannelDetector_OffscreenSelectedTargetRemainsSelected()
    {
        var result = Detect(new[]
        {
            Node("channels___1450076815581380730", "🚒판매모집", selected: true, offscreen: true),
        });
        Assert.Equal(SalesTargetChannelStatus.Selected, result.Status);
    }

    [Fact]
    public void WindowSelection_PrefersDiscordMainTitleDeterministically()
    {
        var selected = DiscordWindowSelectionPolicy.Select(new[]
        {
            Window(2, 20, "Discord", "Updater", "Chrome_WidgetWin_1"),
            Window(3, 30, "Discord", "#🚒판매모집 | Guild - Discord", "Chrome_WidgetWin_1"),
            Window(1, 10, "Discord", "Call", "Chrome_WidgetWin_1"),
        });
        Assert.Equal(30, selected!.WindowHandle);
    }

    [Theory]
    [InlineData("OtherApp", "#🚒판매모집 | Guild - Discord", "Chrome_WidgetWin_1", true)]
    [InlineData("Discord", "#🚒판매모집 | Guild - Discord", "OtherClass", true)]
    [InlineData("Discord", "#🚒판매모집 | Guild - Discord", "Chrome_WidgetWin_1", false)]
    [InlineData("Discord", "", "Chrome_WidgetWin_1", true)]
    public void WindowSelection_RejectsIneligibleCandidates(
        string processName,
        string title,
        string className,
        bool visible)
    {
        var selected = DiscordWindowSelectionPolicy.Select(new[]
        {
            new DiscordWindowCandidate(1, 10, processName, title, className, visible),
        });
        Assert.Null(selected);
    }

    private static (SalesTargetChannelStatus Status, SalesChannelEvidenceSource Evidence)
        Detect(IReadOnlyCollection<DiscordAccessibilityNodeInfo> nodes) =>
        DiscordTargetChannelDetector.Detect(
            string.Empty,
            "1450076815581380730",
            "🚒판매모집",
            nodes);

    private static DiscordAccessibilityNodeInfo Node(
        string id,
        string name,
        bool? selected = null,
        bool offscreen = false) =>
        new(id, name, "ControlType.ListItem", selected, offscreen);

    private static DiscordWindowCandidate Window(
        int processId,
        long handle,
        string processName,
        string title,
        string className) =>
        new(processId, handle, processName, title, className, true);
}
