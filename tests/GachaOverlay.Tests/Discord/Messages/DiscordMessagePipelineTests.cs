using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Discord.Messages;

public sealed class DiscordMessagePipelineTests
{
    private static readonly DiscordTargetChannels Targets = new(
        "guild",
        "Guild",
        "main",
        "🏠메인",
        "sales",
        "🚒판매모집");

    [Fact]
    public void SnapshotOnly_RestoresBothStoresWithSeparateRetentionPolicies()
    {
        var pipeline = new DiscordMessagePipeline();
        Assert.True(pipeline.StartBootstrap(1, Targets));

        var completed = pipeline.CompleteBootstrap(
            1,
            new[] { TestMessageFactory.FullPatch(1, "main") },
            new[] { TestMessageFactory.FullPatch(2, "sales") });

        Assert.True(completed);
        Assert.Single(pipeline.Current.MainChat);
        Assert.Single(pipeline.Current.SalesSource);
        Assert.False(pipeline.Current.IsBootstrapping);
    }

    [Fact]
    public void BufferedCreate_IsReplayedAfterSnapshot()
    {
        var pipeline = StartPipeline();
        pipeline.ReceiveLive(
            1,
            DiscordMessageMutation.Create(TestMessageFactory.FullPatch(2, "main")));

        pipeline.CompleteBootstrap(
            1,
            new[] { TestMessageFactory.FullPatch(1, "main") },
            Array.Empty<DiscordMessagePatch>());

        Assert.Equal(new[] { "1", "2" }, pipeline.Current.MainChat.Select(x => x.MessageId));
    }

    [Fact]
    public void BufferedUpdate_OverridesStaleSnapshotContent()
    {
        var pipeline = StartPipeline();
        pipeline.ReceiveLive(
            1,
            DiscordMessageMutation.Update(TestMessageFactory.ContentPatch(1, "new", "main")));

        pipeline.CompleteBootstrap(
            1,
            new[] { TestMessageFactory.FullPatch(1, "main", "old") },
            Array.Empty<DiscordMessagePatch>());

        Assert.Equal("new", pipeline.Current.MainChat.Single().Content);
    }

    [Fact]
    public void BufferedDelete_RemovesSnapshotMessage()
    {
        var pipeline = StartPipeline();
        pipeline.ReceiveLive(1, DiscordMessageMutation.Delete("1", "main"));

        pipeline.CompleteBootstrap(
            1,
            new[] { TestMessageFactory.FullPatch(1, "main") },
            Array.Empty<DiscordMessagePatch>());

        Assert.Empty(pipeline.Current.MainChat);
    }

    [Fact]
    public void SnapshotAndDuplicateLiveCreate_ProduceOneMessage()
    {
        var pipeline = StartPipeline();
        var patch = TestMessageFactory.FullPatch(1, "main");
        pipeline.ReceiveLive(1, DiscordMessageMutation.Create(patch));

        pipeline.CompleteBootstrap(1, new[] { patch }, Array.Empty<DiscordMessagePatch>());

        Assert.Single(pipeline.Current.MainChat);
    }

    [Fact]
    public void SalesSnapshot_DoesNotApplyChatTwentyMessageLimit()
    {
        var pipeline = StartPipeline();
        var sales = Enumerable.Range(1, 25)
            .Select(id => TestMessageFactory.FullPatch(id, "sales"))
            .ToArray();

        pipeline.CompleteBootstrap(1, Array.Empty<DiscordMessagePatch>(), sales);

        Assert.Equal(25, pipeline.Current.SalesSource.Count);
    }

    [Fact]
    public void OldGenerationCompletion_IsIgnored()
    {
        var pipeline = new DiscordMessagePipeline();
        pipeline.StartBootstrap(1, Targets);
        pipeline.StartBootstrap(2, Targets);

        var oldCompleted = pipeline.CompleteBootstrap(
            1,
            new[] { TestMessageFactory.FullPatch(1, "main") },
            Array.Empty<DiscordMessagePatch>());
        var currentCompleted = pipeline.CompleteBootstrap(
            2,
            new[] { TestMessageFactory.FullPatch(2, "main") },
            Array.Empty<DiscordMessagePatch>());

        Assert.False(oldCompleted);
        Assert.True(currentCompleted);
        Assert.Equal("2", pipeline.Current.MainChat.Single().MessageId);
        Assert.Equal(2, pipeline.Current.Generation);
    }

    [Fact]
    public void OldGenerationLiveEvent_IsIgnored()
    {
        var pipeline = new DiscordMessagePipeline();
        pipeline.StartBootstrap(1, Targets);
        pipeline.StartBootstrap(2, Targets);

        var accepted = pipeline.ReceiveLive(
            1,
            DiscordMessageMutation.Create(TestMessageFactory.FullPatch(1, "main")));

        Assert.False(accepted);
    }

    private static DiscordMessagePipeline StartPipeline()
    {
        var pipeline = new DiscordMessagePipeline();
        Assert.True(pipeline.StartBootstrap(1, Targets));
        return pipeline;
    }
}
