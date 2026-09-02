namespace LSOverlay.Backend.Discord;

internal sealed class TargetGuildFilter
{
    public TargetGuildFilter(ulong targetGuildId)
    {
        if (targetGuildId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetGuildId));
        }

        TargetGuildId = targetGuildId;
    }

    public ulong TargetGuildId { get; }

    public bool Accepts(ulong guildId) => guildId == TargetGuildId;
}
