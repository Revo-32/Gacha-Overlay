using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using LSOverlay.Protocol;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LSOverlay.Backend.CoreClient;

/// <summary>Only trusted canonical source metadata may enter an implementation.
/// Issued IDs must be opaque and message-authorization-bound; no media URL endpoint exists in M2.</summary>
public interface ICoreMediaReferences
{
    string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl);
}
public interface ICoreAnimatedMediaReferences : ICoreMediaReferences
{
    string RegisterEmoji(string messageId,string identity,bool animated);
}

/// <summary>Backend projection of existing domain semantics, never a second Sales parser.</summary>
public sealed partial class CoreSemanticProjection(ICoreMediaReferences media)
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
        var chat = projected.Select((message, index) =>
        {
            var mapped = MapMessage(message, headers[index], authenticatedUserId);
            return mapped with { PresentationHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mapped, OverlayProtocolJson.Options))).ToLowerInvariant() };
        }).ToArray();
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
            Run(message.MessageId, new ChatToken(string.IsNullOrEmpty(reaction.Emoji.EmojiId) ? ChatTokenKind.Text : ChatTokenKind.CustomEmoji, reaction.Emoji.Name,
                reaction.Emoji.EmojiId, false, reaction.Emoji.Animated), viewer), reaction.Count)).ToArray();
        return new CoreRenderMessage(message.MessageId, author, message.CreatedAt, header,
            Runs(message.MessageId, message.Tokens, viewer, message.Media.FirstOrDefault()), message.HasSelfMention,
            Media(message.MessageId, message.Media, message.Stickers), reactions,
            reply is null ? null : new CoreReply(reply.MessageId, reply.ResolvedAuthorName,
                reply.ResolvedContent is null ? "unavailable" : "resolved",
                Runs(message.MessageId, ChatPresentationSynchronizer.TokenizeDiscordMarkup(reply.ResolvedContent ?? "", viewer), viewer)),
            message.ForwardedMessages.Select(forward => new CoreForward(Runs(message.MessageId, forward.Tokens, viewer, forward.Media.FirstOrDefault()),
                Media(message.MessageId, forward.Media, forward.Stickers))).ToArray(),
            message.Attention.ToString(), message.FallbackKind.ToString(), Details: Details(message));
    }

    // Preserve Full's supported non-image fallback information, without shipping
    // arbitrary component payloads, fetch URLs, or client pixel layout decisions.
    private static IReadOnlyList<CoreDetail> Details(ChatMessagePresentation message)
    {
        var details = new List<CoreDetail>();
        foreach (var item in message.RemoteAttachments)
        {
            if (item.IsVoiceMessage)
            {
                var duration = item.DurationSeconds ?? 0;
                var seconds = double.IsFinite(duration) ? Math.Clamp(duration, 0, TimeSpan.MaxValue.TotalSeconds / 2) : 0;
                details.Add(new("voice", TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? "h\\:mm\\:ss" : "m\\:ss")));
            }
            else if (item.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                details.Add(new("attachment", item.FileName ?? "이름 없는 첨부 파일"));
        }
        foreach (var embed in message.RemoteEmbeds)
        {
            var text = new[] { embed.Title, embed.Description }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (text is not null) details.Add(new("embed", text));
        }
        if (message.RemoteMetadata?.Poll is { } poll)
            details.Add(new("poll", (poll.Question ?? "제목 없는 투표") + " · " + string.Join(" · ", poll.Answers.Select(answer => answer.Text).Where(text => !string.IsNullOrWhiteSpace(text)))));
        IEnumerable<DiscordComponentMetadata> Flatten(IEnumerable<DiscordComponentMetadata> components, int depth = 0)
        {
            if (depth > 16) throw new InvalidDataException("Core component nesting limit exceeded.");
            foreach (var component in components)
            {
                yield return component;
                foreach (var child in Flatten(component.Children, depth + 1)) yield return child;
            }
        }
        var labels = Flatten(message.RemoteMetadata?.Components ?? Array.Empty<DiscordComponentMetadata>())
            .Select(component => component.Label ?? component.Content ?? component.Description).Where(text => !string.IsNullOrWhiteSpace(text)).Take(4).ToArray();
        if (labels.Length != 0) details.Add(new("components", string.Join(" · ", labels)));
        return details.Distinct().ToArray();
    }

    private IReadOnlyList<CoreRun> Runs(string messageId, IReadOnlyList<ChatToken> tokens, string viewer) =>
        tokens.Select(token => Run(messageId, token, viewer)).ToArray();

    private IReadOnlyList<CoreRun> Runs(string messageId, IReadOnlyList<ChatToken> tokens, string viewer, ChatMediaCandidate? primary)
    {
        if (primary is null) return Runs(messageId,tokens,viewer);
        var result=new List<CoreRun>();
        foreach (var token in tokens) {
            if (token.Kind!=ChatTokenKind.Text) {result.Add(Run(messageId,token,viewer));continue;}
            var offset=0;
            foreach (Match match in SourceTokenPattern().Matches(token.Text)) {
                // Reuse Full's exact-source relationship/punctuation policy;
                // never suppress another link or an additional hidden preview.
                var remaining=ChatMediaSourcePolicy.SuppressExactSourceToken(match.Value,primary,true,true);
                if (remaining==match.Value) continue;
                var length=match.Length-remaining.Length;
                if (match.Index>offset) result.Add(new CoreRun("Text",token.Text[offset..match.Index]));
                result.Add(new CoreRun("MediaSource",match.Value[..length],MediaId:media.RegisterCanonical(messageId,"attachment",primary.Url,primary.Url)));
                offset=match.Index+length;
            }
            if (offset<token.Text.Length) result.Add(new CoreRun("Text",token.Text[offset..]));
        }
        return result;
    }
    [GeneratedRegex("https://\\S+",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceTokenPattern();

    private CoreRun Run(string messageId, ChatToken token, string viewer) => new(
        token.Kind.ToString(), token.Text, token.Identity,
        token.Kind == ChatTokenKind.Mention && token.Identity == viewer,
        token.IsAnimatedEmoji, token.Kind == ChatTokenKind.CustomEmoji && !string.IsNullOrEmpty(token.Identity)
            ? media is ICoreAnimatedMediaReferences animated ? animated.RegisterEmoji(messageId,token.Identity,token.IsAnimatedEmoji)
                : media.RegisterCanonical(messageId, "emoji", token.Identity, null) : null);

    private IReadOnlyList<LSOverlay.Protocol.CoreMedia> Media(string messageId, IReadOnlyList<ChatMediaCandidate> candidates,
        IReadOnlyList<ChatStickerPresentation> stickers) =>
        candidates.Select(candidate => new LSOverlay.Protocol.CoreMedia(
            media.RegisterCanonical(messageId, "attachment", candidate.Url, candidate.Url), "attachment", candidate.DisplayName,
            candidate.Width, candidate.Height, candidate.ContentType == "image/gif"))
        .Concat(stickers.Select(sticker => new LSOverlay.Protocol.CoreMedia(
            media.RegisterCanonical(messageId, "sticker", sticker.StickerId, sticker.AssetUrl), "sticker", sticker.Name,
            null, null, sticker.FormatType is 2 or 3 or 4))).ToArray();
}
