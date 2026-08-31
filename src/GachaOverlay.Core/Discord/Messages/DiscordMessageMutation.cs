namespace GachaOverlay.Core.Discord.Messages;

public enum DiscordMessageMutationKind
{
    Create,
    Update,
    Delete,
}

public sealed record DiscordMessageMutation
{
    private DiscordMessageMutation(
        DiscordMessageMutationKind kind,
        string messageId,
        string? channelId,
        DiscordMessagePatch? patch)
    {
        Kind = kind;
        MessageId = messageId;
        ChannelId = channelId;
        Patch = patch;
    }

    public DiscordMessageMutationKind Kind { get; }

    public string MessageId { get; }

    public string? ChannelId { get; }

    public DiscordMessagePatch? Patch { get; }

    public static DiscordMessageMutation Create(DiscordMessagePatch patch) =>
        FromPatch(DiscordMessageMutationKind.Create, patch);

    public static DiscordMessageMutation Update(DiscordMessagePatch patch) =>
        FromPatch(DiscordMessageMutationKind.Update, patch);

    public DiscordMessageMutation WithPatch(DiscordMessagePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Kind == DiscordMessageMutationKind.Delete)
        {
            throw new InvalidOperationException("A delete mutation cannot contain a patch.");
        }

        return new DiscordMessageMutation(Kind, MessageId, ChannelId, patch);
    }

    public static DiscordMessageMutation Delete(string messageId, string? channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return new DiscordMessageMutation(
            DiscordMessageMutationKind.Delete,
            messageId,
            channelId,
            null);
    }

    private static DiscordMessageMutation FromPatch(
        DiscordMessageMutationKind kind,
        DiscordMessagePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var channelId = patch.ChannelId.HasValue ? patch.ChannelId.Value : null;
        return new DiscordMessageMutation(kind, patch.MessageId, channelId, patch);
    }
}
