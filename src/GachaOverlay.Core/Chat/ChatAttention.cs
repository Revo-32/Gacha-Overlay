using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Chat;

public enum ChatAttention { Normal, EveryoneHere, ReplyToSelf, DirectSelfMention }

public static class ChatAttentionPolicy
{
    public static ChatAttention Classify(NormalizedDiscordMessage message, string? self)
    {
        if (!string.IsNullOrWhiteSpace(self))
        {
            if (message.Mentions.Any(mention => mention.UserId == self)) return ChatAttention.DirectSelfMention;
            if (message.RemoteMetadata?.Reply is { } reply &&
                !string.Equals(reply.Kind, "Forward", StringComparison.OrdinalIgnoreCase) && reply.ResolvedAuthorId == self)
                return ChatAttention.ReplyToSelf;
        }
        return message.RemoteMetadata?.MentionedEveryone == true ? ChatAttention.EveryoneHere : ChatAttention.Normal;
    }
}
