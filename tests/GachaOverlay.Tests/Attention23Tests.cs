using GachaOverlay.Core.Attention;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Infrastructure.Attention;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests;

public sealed class Attention23Tests
{
    internal static NormalizedDiscordMessage Message(string id = "100", string? mention = null, string? replyAuthor = null,
        bool everyone = false, string content = "내용") => new(id, "10", "sender", "sender", "표시 이름", content,
            DateTimeOffset.Parse("2026-09-08T00:00:00Z"), null, [], [], [],
            mention is null ? [] : [new DiscordMention(mention, "동일한 표시 이름")])
        {
            RemoteMetadata = new("Default", 0, 0, false, false, everyone, false, false,
                replyAuthor is null ? null : new DiscordReplyMetadata("Default", "1", "10", "90")
                { ResolvedAuthorId = replyAuthor, ResolvedAuthorName = "동일한 표시 이름" }, [], [], null),
        };

    [Theory]
    [InlineData("self", null, false, ChatAttention.DirectSelfMention)]
    [InlineData("self", "self", true, ChatAttention.DirectSelfMention)]
    [InlineData(null, "self", false, ChatAttention.ReplyToSelf)]
    [InlineData("other", "self", true, ChatAttention.ReplyToSelf)]
    [InlineData("other", null, false, ChatAttention.Normal)]
    [InlineData(null, null, false, ChatAttention.Normal)]
    [InlineData(null, null, true, ChatAttention.EveryoneHere)]
    public void IdentityHierarchy(string? mention, string? reply, bool everyone, ChatAttention expected) =>
        Assert.Equal(expected, ChatAttentionPolicy.Classify(Message(mention: mention, replyAuthor: reply, everyone: everyone), "self"));

    [Theory]
    [InlineData("@self")]
    [InlineData("<@self>")]
    [InlineData("@everyone")]
    [InlineData("@here")]
    public void RenderedTextNeverGuessesIdentity(string text)
    {
        Assert.Equal(ChatAttention.Normal, ChatAttentionPolicy.Classify(Message(content: text), "self"));
        Assert.Equal(ChatAttention.Normal, ChatAttentionPolicy.Classify(Message(mention: "self"), null));
    }

    [Fact]
    public void HistoryPrioritizesDeduplicatesAndUpdatesWithoutUnreadReset()
    {
        var history = History();
        history.ObserveChat(Message(mention: "self", replyAuthor: "self"), true);
        Assert.Equal(AttentionEventType.DirectSelfMention, Assert.Single(history.Items).Type);
        history.MarkRead("chat:100");
        for (var i = 0; i < 5; i++) history.ObserveChat(Message(mention: "self"), false);
        Assert.True(Assert.Single(history.Items).IsRead);
        history.ObserveChat(Message(replyAuthor: "self", content: "수정된 내용"), true);
        Assert.Equal(AttentionEventType.ReplyToSelf, Assert.Single(history.Items).Type);
        Assert.Equal("수정된 내용", history.Items[0].Preview);
        history.ObserveChat(Message(mention: "other"), true);
        Assert.Empty(history.Items);
    }

    [Fact]
    public void HydrationDoesNotCreateHistoryAndDeleteMinimizesData()
    {
        var history = History();
        history.ObserveChat(Message(mention: "self"), false);
        Assert.Empty(history.Items);
        history.ObserveChat(Message(mention: "self"), true);
        history.RemoveChat("100");
        Assert.Empty(history.Items);
        Assert.DoesNotContain("내용", System.Text.Json.JsonSerializer.Serialize(history.Snapshot()));
    }

    [Theory]
    [InlineData(AttentionEventType.SalesNextTurn)]
    [InlineData(AttentionEventType.SalesCurrentTurn)]
    [InlineData(AttentionEventType.GtaClientDetected)]
    [InlineData(AttentionEventType.GtaClientLost)]
    public void SemanticIdentityDedup(AttentionEventType type)
    {
        var history = History();
        var entry = new AttentionEntry("canonical", type, DateTimeOffset.UtcNow, "source", "context", "preview", false, AttentionPriority.Medium);
        for (var i = 0; i < 10; i++) history.Add(entry);
        Assert.Single(history.Items);
    }

    [Fact]
    public void BoundedDurableReloadAndAccountIsolation()
    {
        using var temporary = new TemporaryDirectory();
        var store = new JsonNotificationStore(temporary.File("attention.json"));
        var history = History();
        for (var i = 0; i < 60; i++) history.ObserveChat(Message(id: i.ToString(), mention: "self", content: new string('가', 500)), true);
        Assert.Equal(30, history.Items.Count);
        Assert.All(history.Items, x => Assert.True(x.Preview.Length <= 180));
        history.MarkRead();
        Assert.True(store.Save(history.Snapshot()));
        var loaded = new NotificationHistory();
        loaded.Restore(store.Load());
        Assert.Equal(30, loaded.Items.Count);
        Assert.All(loaded.Items, x => Assert.True(x.IsRead));
        loaded.SetOwner("different");
        Assert.Empty(loaded.Items);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":99,\"OwnerId\":\"self\",\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"OwnerId\":\"self\",\"Items\":null}")]
    public void MalformedStoreRecovers(string text)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("attention.json");
        File.WriteAllText(path, text);
        Assert.Null(new JsonNotificationStore(path).Load());
    }

    private static NotificationHistory History()
    {
        var history = new NotificationHistory();
        history.SetOwner("self");
        return history;
    }

    [Fact]
    public void OffWindowEditsAndDeletesStillReachHistoryWithoutNewChannelSubscription()
    {
        var history = History();
        history.ObserveChat(Message(mention: "self"), true);
        var pipeline = new DiscordMessagePipeline();
        pipeline.LiveMainMutationAccepted += mutation => history.ObserveMutation(mutation, null);
        pipeline.StartBootstrap(1, new("1", "guild", "10", "main", "20", "sales"));
        pipeline.CompleteBootstrap(1, [], []);
        var patch = new DiscordMessagePatch("100")
        {
            ChannelId = OptionalValue<string>.From("10"),
            Content = OptionalValue<string>.From("원문이 바뀜"),
            Mentions = OptionalValue<IReadOnlyList<DiscordMention>>.From([]),
            RemoteMetadata = OptionalValue<DiscordRemoteMessageMetadata?>.From(null),
        };
        pipeline.ReceiveLive(0, DiscordMessageMutation.Delete("100", "10"));
        pipeline.ReceiveLive(1, DiscordMessageMutation.Delete("100", "unrelated"));
        Assert.Single(history.Items);
        pipeline.ReceiveLive(1, DiscordMessageMutation.Update(patch));
        Assert.Empty(history.Items);
        history.ObserveChat(Message(mention: "self"), true);
        pipeline.ReceiveLive(1, DiscordMessageMutation.Delete("100", "10"));
        Assert.Empty(history.Items);
    }

    [Fact]
    public void MetadataOnlyAttentionChangesReprojectWithoutChangingIdentity()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var normal = Message();
        var state = new DiscordMessageState(1, false, [normal], []);
        Assert.Equal(ChatAttention.Normal, Assert.Single(synchronizer.Synchronize(state, "self")).Message!.Attention);
        var everyone = normal with { RemoteMetadata = normal.RemoteMetadata! with { MentionedEveryone = true } };
        Assert.Equal(ChatAttention.EveryoneHere, Assert.Single(synchronizer.Synchronize(state with { MainChat = [everyone] }, "self")).Message!.Attention);
        var reply = normal with { RemoteMetadata = Message(replyAuthor: "self").RemoteMetadata };
        Assert.Equal(ChatAttention.ReplyToSelf, Assert.Single(synchronizer.Synchronize(state with { MainChat = [reply] }, "self")).Message!.Attention);
    }
}
