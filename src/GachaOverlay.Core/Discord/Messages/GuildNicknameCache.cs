namespace GachaOverlay.Core.Discord.Messages;

public sealed class GuildNicknameCache
{
    private readonly Dictionary<GuildAuthorKey, string> _nicknames = new();

    public int Count => _nicknames.Count;

    public bool TryGet(string guildId, string authorId, out string? nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorId);

        return _nicknames.TryGetValue(new GuildAuthorKey(guildId, authorId), out nickname);
    }

    public void Set(string guildId, string authorId, string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);

        _nicknames[new GuildAuthorKey(guildId, authorId)] = nickname;
    }

    public bool Remove(string guildId, string authorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorId);

        return _nicknames.Remove(new GuildAuthorKey(guildId, authorId));
    }

    private readonly record struct GuildAuthorKey(string GuildId, string AuthorId);
}
