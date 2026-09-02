namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordComponentOptionMetadata(
    string Label,
    string Value,
    string? Description,
    DiscordCustomEmoji? Emoji,
    bool? IsDefault);

public sealed record DiscordComponentMetadata(
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
    IReadOnlyList<DiscordComponentMetadata> Children,
    IReadOnlyList<DiscordComponentOptionMetadata> Options,
    string? UnknownPayload)
{
    public DiscordCustomEmoji? Emoji { get; init; }

    public IReadOnlyDictionary<string, string?> Attributes { get; init; } =
        new Dictionary<string, string?>();
}

public sealed record DiscordPollAnswerMetadata(
    uint AnswerId,
    string? Text,
    DiscordCustomEmoji? Emoji,
    uint? VoteCount,
    bool? SelfVoted);

public sealed record DiscordPollMetadata(
    string? Question,
    IReadOnlyList<DiscordPollAnswerMetadata> Answers,
    DateTimeOffset ExpiresAt,
    bool AllowMultiselect,
    string Layout,
    bool? IsFinalized);

public sealed record DiscordReplyMetadata(
    string Kind,
    string? GuildId,
    string? ChannelId,
    string? MessageId)
{
    public string? ResolvedAuthorName { get; init; }

    public string? ResolvedContent { get; init; }
}

public sealed record DiscordForwardSnapshotMetadata(
    string MessageType,
    string Content,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? EditedAt,
    IReadOnlyList<DiscordAttachmentMetadata> Attachments,
    IReadOnlyList<DiscordEmbedMetadata> Embeds,
    IReadOnlyList<DiscordMention> Mentions,
    IReadOnlyList<DiscordStickerMetadata> Stickers,
    IReadOnlyList<DiscordComponentMetadata> Components);

public sealed record DiscordRemoteMessageMetadata(
    string MessageType,
    int RawMessageType,
    ulong Flags,
    bool IsPinned,
    bool IsTts,
    bool MentionedEveryone,
    bool AuthorIsBot,
    bool AuthorIsWebhook,
    DiscordReplyMetadata? Reply,
    IReadOnlyList<DiscordForwardSnapshotMetadata> ForwardedSnapshots,
    IReadOnlyList<DiscordComponentMetadata> Components,
    DiscordPollMetadata? Poll);
