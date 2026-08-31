namespace GachaOverlay.Core.Discord.Connection;

public sealed record DiscordTargetOptions
{
    public const string DefaultMainChannelName = ProductionServerProfile.DefaultMainChannelName;
    public const string DefaultSalesChannelName = ProductionServerProfile.SalesChannelName;

    public string? GuildId { get; init; }

    public string? MainChannelId { get; init; }

    public string? SalesChannelId { get; init; }

    public string MainChannelName { get; init; } = DefaultMainChannelName;

    public string SalesChannelName { get; init; } = DefaultSalesChannelName;

    public bool RequireConfiguredMainChannel { get; init; }
}
