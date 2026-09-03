using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M94ProductionRemoteModeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task M10_ProductionAllowlistMigratesUnavailableSelectionToMainOrFirstAccessible(bool mainAccessible)
    {
        using var directory = new TemporaryDirectory();
        var main = ulong.Parse(MainChannelPolicy.Ordered[0].Id);
        var firstRoom = ulong.Parse(MainChannelPolicy.Ordered[2].Id);
        var store = CreateStore(directory, AppSettings.CreateDefault() with { RemoteSelectedChannelId = "100" });
        var fake = new FakeRemoteClient
        {
            ChannelCatalogOverride = new[]
            {
                new ChatChannelDescriptor(10, firstRoom, "Renamed Room", 1, false),
                new ChatChannelDescriptor(10, mainAccessible ? main : 999, "메인", 99, false),
            },
        };
        var pipeline = new DiscordMessagePipeline();
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore("token"),
            pipeline, Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => fake,
            channelPolicy: MainChannelPolicy.Apply);
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);
        Assert.Equal((mainAccessible ? main : firstRoom).ToString(), coordinator.Snapshot.SelectedChannelId);
        Assert.Equal(coordinator.Snapshot.SelectedChannelId, store.Current.RemoteSelectedChannelId);
        Assert.All(pipeline.Current.MainChat, message => Assert.Equal(coordinator.Snapshot.SelectedChannelId, message.ChannelId));
        Assert.Equal(mainAccessible ? 2 : 1, coordinator.Snapshot.Channels.Count);
    }

    [Fact]
    public async Task M10_NoAccessibleProductChannelIsControlledUnavailableAndDoesNotPickNameLookalike()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault());
        var pipeline = new DiscordMessagePipeline();
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore("token"),
            pipeline, Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => new FakeRemoteClient(),
            channelPolicy: MainChannelPolicy.Apply);
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.ChannelSelectionRequired);
        Assert.Empty(coordinator.Snapshot.Channels);
        Assert.Null(store.Current.RemoteSelectedChannelId);
        Assert.Empty(pipeline.Current.MainChat);
    }

    [Fact]
    public async Task M10_ChannelFeedbackOccursOnlyAfterSuccessfulAtomicCommit()
    {
        using var directory = new TemporaryDirectory();
        var main = MainChannelPolicy.Ordered[0].Id;
        var next = MainChannelPolicy.Ordered[1].Id;
        var store = CreateStore(directory, AppSettings.CreateDefault() with { RemoteSelectedChannelId = main });
        var fake = new FakeRemoteClient(delaySwitchReady: true)
        {
            ChannelCatalogOverride = new[]
            {
                new ChatChannelDescriptor(10, ulong.Parse(next), "Heist", 0, false),
                new ChatChannelDescriptor(10, ulong.Parse(main), "Main", 99, false),
            },
        };
        var pipeline = new DiscordMessagePipeline();
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore("token"),
            pipeline, Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => fake,
            channelPolicy: MainChannelPolicy.Apply);
        var committed = new List<string>();
        coordinator.ChannelSwitchCommitted += committed.Add;
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);
        Assert.Empty(committed);
        Assert.False(await coordinator.SwitchChannelAsync("999"));
        Assert.Empty(committed);
        Assert.True(await coordinator.SwitchChannelAsync(next));
        await fake.SwitchRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(main, store.Current.RemoteSelectedChannelId);
        Assert.All(pipeline.Current.MainChat, message => Assert.Equal(main, message.ChannelId));
        Assert.Empty(committed);
        fake.CompleteSwitch();
        await WaitUntilAsync(() => coordinator.Snapshot.SelectedChannelId == next);
        Assert.Single(committed);
        Assert.Equal(next, store.Current.RemoteSelectedChannelId);
        Assert.All(pipeline.Current.MainChat, message => Assert.Equal(next, message.ChannelId));
    }
}
