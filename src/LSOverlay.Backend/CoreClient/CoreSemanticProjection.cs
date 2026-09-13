using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.CoreClient;

/// <summary>Only trusted canonical source metadata may enter an implementation.
/// Issued IDs must be opaque and message-authorization-bound; no media URL endpoint exists in M2.</summary>
public interface ICoreMediaReferences
{
    string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl);
}

/// <summary>Backend projection of existing domain semantics, never a second Sales parser.</summary>
public sealed class CoreSemanticProjection(ICoreMediaReferences media)
{
    public CoreSnapshot Capture(string generation, long revision, string authenticatedUserId,
        IReadOnlyList<NormalizedDiscordMessage> messages, SalesQueueSnapshot sales,
        IReadOnlyList<HostPresenceSnapshot> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedUserId);
        // Fresh, request-owned projection avoids mutable cross-viewer mention/role state.
        // M3 can retain a bounded per-connection synchronizer for deltas.
        var changes = new ChatPresentationSynchronizer().Synchronize(
            new DiscordMessageState(1, false, messages, Array.Empty<NormalizedDiscordMessage>()), authenticatedUserId);
        var projected = changes.Select(change => change.Message!).ToArray();
        var headers = ChatAuthorGrouping.ResolveHeaders(projected.Select(message => message.AuthorId));
        var chat = projected.Select((message, index) => MapMessage(message, headers[index], authenticatedUserId)).ToArray();
        var queue = sales.ActiveItems.Select(entry => new CoreSale(entry.MessageId, entry.AuthorId,
            entry.DisplayName, entry.AllProducts.Select(product => new CoreSaleProduct(
                product.ProductId, product.DisplayName, product.EmojiId, product.EmojiName, product.Quantity)).ToArray(),
            entry.ObservationTrust.ToString(), entry.CreatedAt,
            Runs(entry.MessageId, ChatPresentationSynchronizer.TokenizeDiscordMarkup(entry.DetailSource ?? "", authenticatedUserId), authenticatedUserId))).ToArray();
        var salesState = new CoreSalesState(sales.Revision, sales.ObservationStatus.ToString(), sales.IsTrackingEnabled,
            queue, sales.CurrentSeller?.MessageId, sales.NextWaitingEntry?.MessageId, sales.WaitingCount,
            sales.CurrentSeller?.AuthorId == authenticatedUserId, sales.NextWaitingEntry?.AuthorId == authenticatedUserId,
            sales.ContainsUnverifiedActiveItems);
        return new CoreSnapshot(OverlayTransportProtocol.Version, generation, revision, authenticatedUserId,
            chat, salesState, sessions.ToArray());
    }

    private CoreRenderMessage MapMessage(ChatMessagePresentation message, bool header, string viewer)
    {
        var icon = message.AuthorStyle?.Icon;
        var author = new CoreAuthor(message.AuthorId, message.AuthorName, message.AuthorStyle?.Color,
            icon is { Kind: not "unicode" } ? media.RegisterCanonical(message.MessageId, "role", icon.Value, icon.Url) : null,
            icon is { Kind: "unicode" } ? icon.Value : null);
        var reply = message.RemoteMetadata?.Reply;
        var reactions = message.Reactions.Select(reaction => new CoreReaction(
            Run(message.MessageId, new ChatToken(ChatTokenKind.CustomEmoji, reaction.Emoji.Name,
                reaction.Emoji.EmojiId, false, reaction.Emoji.Animated), viewer), reaction.Count)).ToArray();
        return new CoreRenderMessage(message.MessageId, author, message.CreatedAt, header,
            Runs(message.MessageId, message.Tokens, viewer), message.HasSelfMention,
            Media(message.MessageId, message.Media, message.Stickers), reactions,
            reply is null ? null : new CoreReply(reply.MessageId, reply.ResolvedAuthorName,
                reply.ResolvedContent is null ? "unavailable" : "resolved",
                Runs(message.MessageId, ChatPresentationSynchronizer.TokenizeDiscordMarkup(reply.ResolvedContent ?? "", viewer), viewer)),
            message.ForwardedMessages.Select(forward => new CoreForward(Runs(message.MessageId, forward.Tokens, viewer),
                Media(message.MessageId, forward.Media, forward.Stickers))).ToArray());
    }

    private IReadOnlyList<CoreRun> Runs(string messageId, IReadOnlyList<ChatToken> tokens, string viewer) =>
        tokens.Select(token => Run(messageId, token, viewer)).ToArray();

    private CoreRun Run(string messageId, ChatToken token, string viewer) => new(
        token.Kind.ToString(), token.Text, token.Identity,
        token.Kind == ChatTokenKind.Mention && token.Identity == viewer,
        token.IsAnimatedEmoji, token.Kind == ChatTokenKind.CustomEmoji && !string.IsNullOrEmpty(token.Identity)
            ? media.RegisterCanonical(messageId, "emoji", token.Identity, null) : null);

    private IReadOnlyList<CoreMedia> Media(string messageId, IReadOnlyList<ChatMediaCandidate> candidates,
        IReadOnlyList<ChatStickerPresentation> stickers) =>
        candidates.Select(candidate => new CoreMedia(
            media.RegisterCanonical(messageId, "attachment", candidate.Url, candidate.Url), "attachment", candidate.DisplayName,
            candidate.Width, candidate.Height, candidate.ContentType == "image/gif"))
        .Concat(stickers.Select(sticker => new CoreMedia(
            media.RegisterCanonical(messageId, "sticker", sticker.StickerId, sticker.AssetUrl), "sticker", sticker.Name,
            null, null, sticker.FormatType is 2 or 3 or 4))).ToArray();
}
