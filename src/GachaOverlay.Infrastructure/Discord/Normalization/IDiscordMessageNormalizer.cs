using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Infrastructure.Discord.Normalization;

public interface IDiscordMessageNormalizer
{
    IReadOnlyList<DiscordMessagePatch> NormalizeSnapshot(
        JsonElement getChannelResponse,
        string channelId,
        string? guildIdHint = null);

    bool TryNormalizeDispatch(
        JsonElement dispatch,
        out DiscordMessageMutation? mutation,
        out string eventName,
        string? guildIdHint = null);

    bool TryNormalizeForwardSource(
        JsonElement getChannelResponse,
        DiscordForwardSourceKey sourceKey,
        out DiscordForwardContent? content);
}
