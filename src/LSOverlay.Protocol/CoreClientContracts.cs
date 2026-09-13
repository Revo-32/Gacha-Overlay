using System.Text.Json.Serialization;

namespace LSOverlay.Protocol;

/// <summary>Opt-in Core projection; never serialized inside the legacy Full envelopes.</summary>
public static class CoreClientProtocol
{
    public const string Capability = "core_render_v1";
    public const string SessionStart = "core_session_start_v1";
    public const string SnapshotChunk = "core_snapshot_chunk_v1";
    public const string Ready = "core_ready_v1";
    public const int MaximumSnapshotBytes = 1024 * 1024;
    public const int MaximumChunks = 128;
    public const int ChunkPayloadBytes = 9000;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CoreSessionStart(
    int ProtocolVersion, string Type, IReadOnlyList<string> Capabilities,
    string? Generation = null, long? AfterRevision = null);

public sealed record CoreRun(string Kind, string Text, string? Identity = null,
    bool IsSelf = false, bool IsAnimated = false, string? MediaId = null);
public sealed record CoreAuthor(string Id, string DisplayName, uint? Color,
    string? IconMediaId = null, string? IconUnicode = null);
public sealed record CoreMedia(string Id, string Kind, string? Name,
    int? Width, int? Height, bool IsAnimated);
public sealed record CoreReply(string? MessageId, string? AuthorName, string Status, IReadOnlyList<CoreRun> Runs);
public sealed record CoreReaction(CoreRun Emoji, int Count);
public sealed record CoreDetail(string Kind, string Text);
public sealed record CoreForward(IReadOnlyList<CoreRun> Runs, IReadOnlyList<CoreMedia> Media);
public sealed record CoreRenderMessage(string Id, CoreAuthor Author, DateTimeOffset? CreatedAt,
    bool ShowAuthorHeader, IReadOnlyList<CoreRun> Runs, bool HasSelfMention,
    IReadOnlyList<CoreMedia> Media, IReadOnlyList<CoreReaction> Reactions,
    CoreReply? Reply, IReadOnlyList<CoreForward> Forwarded,
    string Attention = "Normal", string FallbackKind = "None", string PresentationHash = "",
    IReadOnlyList<CoreDetail>? Details = null);
public sealed record CoreSale(string MessageId, string AuthorId, string DisplayName,
    IReadOnlyList<CoreSaleProduct> Products, string Trust, DateTimeOffset? CreatedAt,
    IReadOnlyList<CoreRun> DetailRuns);
public sealed record CoreSaleProduct(string Id, string Name, string EmojiId, string EmojiName, int Quantity);
public sealed record CoreSalesState(long Revision, string ObservationStatus, bool IsTrackingEnabled,
    IReadOnlyList<CoreSale> Queue, string? CurrentMessageId, string? NextMessageId,
    int WaitingCount, bool CurrentIsSelf, bool NextIsSelf, bool ContainsUnverifiedItems,
    CoreSalesPresentation? Presentation = null, CoreSalesActions? Actions = null);
// Display hints, not authorization. The status endpoint rechecks the current
// identity, owner, channel permission and upstream generation on every command.
public sealed record CoreSalesActions(string Generation, long Sequence, IReadOnlyList<CoreSalesActionTarget> Targets);
public sealed record CoreSalesActionTarget(string MessageId, bool CanComplete, bool CanUndo, bool BotCompleted);
public sealed record CoreSalesPresentation(string ContentMode, string HealthMode, string AccentKind,
    string IconKind, string PrimaryText, string SecondaryText, string StatusText, bool IsVisible,
    bool IsTrustedForNewPersonalAlert, IReadOnlyList<string> CompletionEnabledMessageIds);
public sealed record CoreSnapshot(int ProtocolVersion, string Generation, long Revision,
    string SelfUserId, IReadOnlyList<CoreRenderMessage> Chat, CoreSalesState Sales,
    IReadOnlyList<HostPresenceSnapshot> Session, string? ChatConnectionState = null,
    CoreChatSelection? ChatSelection = null);
public sealed record CoreChatSelection(int Slot, string Name, IReadOnlyList<int> AvailableSlots);

// Base64 chunking preserves exact UTF-8, including a single large message. The
// receiver validates sequence/hash and publishes only the complete immutable doc.
public sealed record CoreSnapshotChunk(int ProtocolVersion, string Type, string SnapshotId,
    string Generation, long Revision, int Index, int Count, int TotalBytes,
    string Sha256, string PayloadBase64);
public sealed record CoreReady(int ProtocolVersion, string Type, string Generation, long Revision, bool Resumed);
