using Discord;
using Discord.WebSocket;
using LSOverlay.Backend;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using Microsoft.Extensions.DependencyInjection;

namespace GachaOverlay.Tests.Backend;

public sealed class DiscordGatewayPolicyTests
{
    [Fact]
    public void RequiredIntents_AreExactAndExcludeGuildMembers()
    {
        var expected = GatewayIntents.Guilds |
            GatewayIntents.GuildMessages |
            GatewayIntents.GuildMessageReactions |
            GatewayIntents.GuildMessagePolls |
            GatewayIntents.MessageContent |
            GatewayIntents.GuildPresences;

        Assert.Equal(expected, DiscordGatewayPolicy.RequiredIntents);
        Assert.Equal(GatewayIntents.None, DiscordGatewayPolicy.RequiredIntents & GatewayIntents.GuildMembers);
        Assert.Equal(GatewayIntents.None, DiscordGatewayPolicy.RequiredIntents & GatewayIntents.DirectMessages);
    }

    [Fact]
    public void SocketConfiguration_DisablesMessageAndUserPrefetchCaches()
    {
        var config = DiscordGatewayPolicy.CreateSocketConfiguration();

        Assert.Equal(0, config.MessageCacheSize);
        Assert.False(config.AlwaysDownloadUsers);
        Assert.False(config.AlwaysDownloadDefaultStickers);
        Assert.False(config.AlwaysResolveStickers);
        Assert.False(config.IncludeRawPayloadOnGatewayErrors);
        Assert.Equal(DiscordGatewayPolicy.RequiredIntents, config.GatewayIntents);
    }

    [Fact]
    public void TargetGuildFilter_AcceptsOnlyConfiguredGuild()
    {
        var filter = new TargetGuildFilter(123);

        Assert.True(filter.Accepts(123));
        Assert.False(filter.Accepts(122));
        Assert.False(filter.Accepts(0));
    }

    [Fact]
    public void DiscordNetVersionAndCapabilities_ArePinnedAndAvailable()
    {
        Assert.Equal(new Version(3, 20, 1, 0), typeof(DiscordSocketClient).Assembly.GetName().Version);
        var capability = DiscordSdkCapabilityAudit.Inspect();

        Assert.True(DiscordSdkCapabilityAudit.HasRequiredSurface(capability));
        Assert.True(capability.ForwardedMessages);
        Assert.True(capability.MessageSnapshot);
        Assert.True(capability.Stickers);
        Assert.True(capability.Attachments);
        Assert.True(capability.Embeds);
        Assert.True(capability.Components);
        Assert.True(capability.ReferencedMessage);
        Assert.True(capability.Poll);
    }

    [Fact]
    public void HostComposition_UsesOneDiscordSocketClientWithoutStartingNetwork()
    {
        var configuration = new BackendConfiguration(
            new BackendBotCredential("synthetic-token"),
            123,
            Array.Empty<ulong>());
        using var host = Program.CreateHost(configuration);

        var first = host.Services.GetRequiredService<DiscordSocketClient>();
        var second = host.Services.GetRequiredService<DiscordSocketClient>();

        Assert.Same(first, second);
        Assert.Equal(ConnectionState.Disconnected, first.ConnectionState);
    }
}
