using System.Text.Json;
using System.Text.Json.Serialization;

namespace LSOverlay.Protocol;

public static class OverlayTransportProtocol
{
    public const int Version = 1;
    public const string WebSocketSubprotocol = "ls-overlay.v1";
    public const int MaximumInboundWebSocketBytes = 16 * 1024;

    public const string HostPresenceChanged = "host_presence_changed";
    public const string Resume = "resume";
    public const string Event = "event";
    public const string Live = "live";
    public const string ResyncRequired = "resync_required";
    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeat_ack";

    public const string ChatSubscribe = "chat_subscribe";
    public const string ChatReady = "chat_ready";
    public const string ChatFailed = "chat_failed";
    public const string ChatResyncRequired = "chat_resync_required";
    public const string ChatMessageCreate = "chat_message_create";
    public const string ChatMessageUpdate = "chat_message_update";
    public const string ChatMessageDelete = "chat_message_delete";
    public const string ChatAccessRevoked = "chat_access_revoked";
    public const string ChatAuthorizationUnavailable = "chat_authorization_unavailable";
    public const string ChatChannelUnavailable = "chat_channel_unavailable";

    public const string SalesSubscribe = "sales_subscribe";
    public const string SalesReady = "sales_ready";
    public const string SalesFailed = "sales_failed";
    public const string SalesResyncRequired = "sales_resync_required";
    public const string SalesMessageCreate = "sales_message_create";
    public const string SalesMessageUpdate = "sales_message_update";
    public const string SalesMessageDelete = "sales_message_delete";
    public const string SalesCompletionEvidence = "sales_completion_evidence";
    public const string SalesAccessRevoked = "sales_access_revoked";
    public const string SalesAuthorizationUnavailable = "sales_authorization_unavailable";
    public const string SalesChannelUnavailable = "sales_channel_unavailable";

    public const string GtaCompanionSnapshot = "gta_companion_snapshot";
    public const string GtaCompanionV1Capability = "gta_companion_v1";
}

public static class GtaCompanionProtocolPolicy
{
    public const ulong ProductionEventChannelId = 1417898156187713577;
    public const int MaximumBonuses = 64;
    public const int MaximumDiscounts = 128;
    public const int MaximumFreeItems = 32;
    public const int MaximumDetailSections = 32;
    public const int MaximumUpcomingWeeks = 8;
    public const int MaximumCampaignEntries = 16;
    public const int MaximumTitleLength = 256;
    public const int MaximumDetailLength = 1024;
}

public static class RemoteSalesPolicy
{
    public const ulong ProductionSalesChannelId = 1450076815581380730;
    public const ulong SellingEmojiId = 1523085309443571762;
    public const string SellingEmojiIdText = "1523085309443571762";
    public const string SellingEmojiName = "SELL_onsale";
    public const ulong NegotiatingEmojiId = 1524773310288756869;
    public const string NegotiatingEmojiIdText = "1524773310288756869";
    public const string NegotiatingEmojiName = "SELL_working";
    public const ulong SoldEmojiId = 1451583544295034940;
    public const string SoldEmojiIdText = "1451583544295034940";
    public const string SoldEmojiName = "SOLD";
    public const ulong ClosedEmojiId = 1418284521337651321;
    public const string ClosedEmojiIdText = "1418284521337651321";
    public const string ClosedEmojiName = "closed";

    public static bool IsSoldMarker(ulong? id, string? name) =>
        id.HasValue ? id.Value == SoldEmojiId : name == SoldEmojiName;

    public static bool IsClosedMarker(ulong? id, string? name) =>
        id.HasValue ? id.Value == ClosedEmojiId : name == ClosedEmojiName;

    public static bool IsSellingMarker(ulong? id, string? name) =>
        id.HasValue ? id.Value == SellingEmojiId : name == SellingEmojiName;

    public static bool IsNegotiatingMarker(ulong? id, string? name) =>
        id.HasValue ? id.Value == NegotiatingEmojiId : name == NegotiatingEmojiName;
}

public enum HostPresenceState
{
    AwaitingPresence,
    Offline,
    OnlineButNotGtaOnline,
    GtaOnline,
}

public sealed record HostPresenceSnapshot(
    int HostSlot,
    HostPresenceState State,
    int? CurrentPlayers,
    int? MaximumPlayers,
    DateTimeOffset ObservedAt);

public sealed record BootstrapResponse(
    int ProtocolVersion,
    string Generation,
    long LatestSequence,
    ulong SelfDiscordUserId,
    IReadOnlyList<HostPresenceSnapshot> TrackedHosts);

public sealed record ProtocolEventEnvelope(
    int ProtocolVersion,
    string Generation,
    long Sequence,
    string EventType,
    HostPresenceSnapshot Payload);

public sealed record ChatChannelDescriptor(
    ulong GuildId,
    ulong ChannelId,
    string Name,
    int Position,
    bool IsAnnouncement);

public sealed record ChatChannelCatalogResponse(
    int ProtocolVersion,
    IReadOnlyList<ChatChannelDescriptor> Channels);

public sealed record ChatBootstrapRequest(
    int ProtocolVersion,
    ulong ChannelId);

public sealed record ChatBootstrapResponse(
    int ProtocolVersion,
    ChatChannelDescriptor Channel,
    string Generation,
    long LatestSequence,
    IReadOnlyList<ChatMessage> RecentMessages);

public sealed record ChatAuthor(
    ulong UserId,
    string Username,
    string? DisplayName,
    string? GuildNickname,
    bool IsBot,
    bool IsWebhook)
{
    public ChatAuthorStyle? RoleStyle { get; init; }
}

public sealed record ChatAuthorStyle(
    ulong? ColorRoleId,
    uint? Color,
    ulong? IconRoleId,
    ChatRoleIcon? Icon);

public sealed record ChatRoleIcon(
    string Kind,
    string Value,
    string? Url = null);

public sealed record ChatEmoji(
    ulong? Id,
    string Name,
    bool IsAnimated,
    string? Url = null);

public sealed record ChatReaction(
    ChatEmoji Emoji,
    int Count);

public sealed record ChatMention(
    string Kind,
    ulong Id,
    string? DisplayName = null);

public sealed record ChatAttachment(
    ulong Id,
    string FileName,
    string Url,
    string ProxyUrl,
    int Size,
    string? ContentType,
    int? Width,
    int? Height,
    string? Description,
    string? Title,
    bool IsEphemeral,
    double? DurationSeconds,
    string? WaveformBase64,
    bool IsVoiceMessage);

public sealed record ChatEmbedField(string Name, string Value, bool IsInline);

public sealed record ChatEmbed(
    string Type,
    string? Url,
    string? Title,
    string? Description,
    DateTimeOffset? Timestamp,
    uint? Color,
    string? ImageUrl,
    string? ThumbnailUrl,
    string? VideoUrl,
    string? AuthorName,
    string? AuthorUrl,
    string? FooterText,
    string? ProviderName,
    IReadOnlyList<ChatEmbedField> Fields);

public sealed record ChatSticker(
    ulong Id,
    string Name,
    string Format,
    string? AssetUrl);

public sealed record ChatMessageReference(
    string Kind,
    ulong? GuildId,
    ulong? ChannelId,
    ulong? MessageId,
    ChatMessage? ResolvedMessage);

public sealed record ChatForwardSnapshot(
    string MessageType,
    string Content,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? EditedAt,
    IReadOnlyList<ChatAttachment> Attachments,
    IReadOnlyList<ChatEmbed> Embeds,
    IReadOnlyList<ChatMention> Mentions,
    IReadOnlyList<ChatSticker> Stickers,
    IReadOnlyList<ChatComponent> Components);

public sealed record ChatComponent(
    string Type,
    int RawType,
    int? Id,
    string? CustomId,
    string? Label,
    string? Content,
    string? Description,
    string? Url,
    string? Value,
    bool? IsDisabled,
    bool? IsSpoiler,
    IReadOnlyList<ChatComponent> Children,
    IReadOnlyList<ChatComponentOption> Options,
    string? UnknownPayload = null)
{
    public ChatEmoji? Emoji { get; init; }

    public IReadOnlyDictionary<string, string?> Attributes { get; init; } =
        new Dictionary<string, string?>();
}

public sealed record ChatComponentOption(
    string Label,
    string Value,
    string? Description,
    ChatEmoji? Emoji,
    bool? IsDefault);

public sealed record ChatPollAnswer(
    uint AnswerId,
    string? Text,
    ChatEmoji? Emoji,
    uint? VoteCount,
    bool? SelfVoted);

public sealed record ChatPoll(
    string? Question,
    IReadOnlyList<ChatPollAnswer> Answers,
    DateTimeOffset ExpiresAt,
    bool AllowMultiselect,
    string Layout,
    bool? IsFinalized);

public sealed record ChatMessage(
    ulong MessageId,
    ulong GuildId,
    ulong ChannelId,
    string MessageType,
    int RawMessageType,
    ChatAuthor? Author,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool IsPinned,
    bool IsTts,
    bool MentionedEveryone,
    ulong Flags,
    IReadOnlyList<ChatEmoji> CustomEmojis,
    IReadOnlyList<ChatAttachment> Attachments,
    IReadOnlyList<ChatEmbed> Embeds,
    IReadOnlyList<ChatMention> Mentions,
    IReadOnlyList<ChatSticker> Stickers,
    IReadOnlyList<ChatForwardSnapshot> ForwardedSnapshots,
    ChatMessageReference? Reference,
    IReadOnlyList<ChatComponent> Components,
    ChatPoll? Poll)
{
    public IReadOnlyList<ChatReaction> Reactions { get; init; } =
        Array.Empty<ChatReaction>();
}

public sealed record ChatMutationEnvelope(
    int ProtocolVersion,
    string Generation,
    long Sequence,
    string EventType,
    ulong ChannelId,
    ulong MessageId,
    ChatMessage? Message);

public enum SalesBootstrapCoverage
{
    Complete,
    Truncated,
    Unavailable,
}

public enum SalesEvidenceCoverage
{
    Complete,
    Partial,
    Unavailable,
}

public enum SalesStatus
{
    Selling,
    Negotiating,
    Completed,
    Clear,
}

public enum SalesStatusActionDisposition
{
    Accepted,
    NoOp,
    RejectedUnauthorized,
    RejectedNotOwner,
    RejectedMessageMissing,
    RejectedInvalidState,
    RejectedUnavailable,
    RejectedRateLimited,
    RejectedStale,
    Failed,
}

public sealed record SalesBootstrapRequest(int ProtocolVersion);

public sealed record SalesStatusActionRequest(
    int ProtocolVersion,
    ulong MessageId,
    SalesStatus DesiredStatus,
    Guid ClientRequestId,
    string SalesGeneration);

public sealed record SalesStatusActionResponse(
    int ProtocolVersion,
    Guid ClientRequestId,
    SalesStatusActionDisposition Disposition,
    bool AwaitingOfficialReadBack);

public sealed record SalesCompletionObservation(
    ulong MessageId,
    bool SoldMarkerPresent,
    bool ClosedMarkerPresent,
    SalesEvidenceCoverage Coverage,
    DateTimeOffset ObservedAt,
    bool BotSellingMarkerPresent = false,
    bool BotNegotiatingMarkerPresent = false,
    bool BotCompletedMarkerPresent = false)
{
    public bool IsSold => SoldMarkerPresent || ClosedMarkerPresent;

    public bool HasAnyBotStatus =>
        BotSellingMarkerPresent ||
        BotNegotiatingMarkerPresent ||
        BotCompletedMarkerPresent;

    public bool MatchesBotStatus(SalesStatus status) => status switch
    {
        SalesStatus.Selling =>
            BotSellingMarkerPresent &&
            !BotNegotiatingMarkerPresent &&
            !BotCompletedMarkerPresent,
        SalesStatus.Negotiating =>
            !BotSellingMarkerPresent &&
            BotNegotiatingMarkerPresent &&
            !BotCompletedMarkerPresent,
        SalesStatus.Completed =>
            !BotSellingMarkerPresent &&
            !BotNegotiatingMarkerPresent &&
            BotCompletedMarkerPresent,
        SalesStatus.Clear => !HasAnyBotStatus,
        _ => false,
    };
}

public sealed record SalesBootstrapResponse(
    int ProtocolVersion,
    ChatChannelDescriptor Channel,
    string Generation,
    long LatestSequence,
    IReadOnlyList<ChatMessage> RecentMessages,
    IReadOnlyList<SalesCompletionObservation> CompletionObservations,
    SalesBootstrapCoverage Coverage);

public sealed record SalesMutationEnvelope(
    int ProtocolVersion,
    string Generation,
    long Sequence,
    string EventType,
    ulong ChannelId,
    ulong MessageId,
    ChatMessage? Message,
    SalesCompletionObservation? CompletionObservation);

public enum GtaCompanionDataState
{
    Available,
    Preparing,
    Unavailable,
}

public enum GtaCompanionItemKind
{
    Bonus,
    Discount,
    FreeItem,
    LoginReward,
    RotatingContent,
    Note,
}

public sealed record GtaCompanionChallenge(
    string ChallengeKey,
    string DisplayTextKo,
    string? RewardTextKo,
    IReadOnlyList<string> RequirementsKo);

public sealed record GtaCompanionItem(
    string ItemKey,
    GtaCompanionItemKind Kind,
    string DisplayTextKo,
    string OriginalLabel,
    int? Multiplier,
    int? DiscountPercent,
    IReadOnlyList<string> RewardTypes,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo);

public sealed record GtaCompanionWeek(
    string WeekKey,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    string? ThemeKo,
    GtaCompanionChallenge? WeeklyChallenge,
    IReadOnlyList<GtaCompanionItem> Bonuses,
    IReadOnlyList<GtaCompanionItem> Discounts,
    IReadOnlyList<GtaCompanionItem> FreeItems,
    IReadOnlyList<GtaCompanionItem> OtherEvents);

public sealed record GtaCompanionCampaignWeek(
    string WeekKey,
    string DisplayTextKo,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo);

public sealed record GtaCompanionCampaign(
    string CampaignKey,
    string TitleKo,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    IReadOnlyList<string> GoalsKo,
    IReadOnlyList<string> RewardsKo,
    IReadOnlyList<GtaCompanionCampaignWeek> UpcomingWeeks);

public sealed record GtaCompanionSnapshot(
    int ProtocolVersion,
    long Revision,
    GtaCompanionDataState State,
    DateTimeOffset GeneratedAt,
    GtaCompanionWeek? CurrentWeek,
    GtaCompanionCampaign? Campaign,
    bool IsTruncated = false);

public sealed record StreamClientMessage(
    int ProtocolVersion,
    string Type,
    string? Generation = null,
    long? AfterSequence = null,
    string? HeartbeatId = null,
    ulong? ChannelId = null,
    string? ChatGeneration = null,
    long? AfterChatSequence = null,
    long? SwitchGeneration = null,
    string? SalesGeneration = null,
    long? AfterSalesSequence = null,
    IReadOnlyList<string>? Capabilities = null);

public sealed record StreamServerMessage(
    int ProtocolVersion,
    string Type,
    string? Generation = null,
    long? LatestSequence = null,
    ProtocolEventEnvelope? Event = null,
    string? HeartbeatId = null,
    DateTimeOffset? SentAt = null,
    string? Reason = null,
    ulong? ChannelId = null,
    string? ChatGeneration = null,
    long? ChatLatestSequence = null,
    long? SwitchGeneration = null,
    ChatMutationEnvelope? ChatEvent = null,
    string? SalesGeneration = null,
    long? SalesLatestSequence = null,
    SalesMutationEnvelope? SalesEvent = null,
    GtaCompanionSnapshot? GtaCompanion = null);

public static class OverlayProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static void EnsureVersion(int version)
    {
        if (version != OverlayTransportProtocol.Version)
        {
            throw new NotSupportedException($"Unsupported LS Overlay protocol version: {version}.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class TransportEndpointSecurity
{
    public static bool IsAllowed(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            return false;
        }

        if (endpoint.Scheme is "https" or "wss")
        {
            return true;
        }

        return endpoint.Scheme is "http" or "ws" && IsLoopbackHost(endpoint.Host);
    }

    public static void EnsureAllowed(Uri endpoint)
    {
        if (!IsAllowed(endpoint))
        {
            throw new InvalidOperationException(
                "Public LS Overlay endpoints require HTTPS/WSS; insecure transport is loopback-only.");
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) &&
        System.Net.IPAddress.IsLoopback(address);
}
