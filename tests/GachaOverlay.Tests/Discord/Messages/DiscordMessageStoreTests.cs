using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Discord.Messages;

public sealed class DiscordMessageStoreTests
{
    [Fact]
    public void Create_AddsMessage()
    {
        var store = new DiscordMessageStore();

        var result = store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(1)));

        Assert.Equal(MessageStoreMutationResult.Applied, result);
        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("1", out _));
    }

    [Fact]
    public void DuplicateCreate_DoesNotCreateDuplicate()
    {
        var store = new DiscordMessageStore();
        var mutation = DiscordMessageMutation.Create(TestMessageFactory.FullPatch(1));

        store.Apply(mutation);
        store.Apply(mutation);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Update_MergesIntoSameMessageId()
    {
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(1, content: "before")));

        store.Apply(DiscordMessageMutation.Update(TestMessageFactory.ContentPatch(1, "after")));

        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("1", out var message));
        Assert.Equal("after", message!.Content);
        Assert.Equal("author-1", message.AuthorId);
    }

    [Fact]
    public void Delete_RemovesMessage()
    {
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(1)));

        var result = store.Apply(DiscordMessageMutation.Delete("1", "main"));

        Assert.Equal(MessageStoreMutationResult.Removed, result);
        Assert.Empty(store.GetOrderedSnapshot());
    }

    [Fact]
    public void UpdateUnknown_WithPartialPayload_IsIgnored()
    {
        var store = new DiscordMessageStore();

        var result = store.Apply(
            DiscordMessageMutation.Update(TestMessageFactory.ContentPatch(99, "partial")));

        Assert.Equal(MessageStoreMutationResult.Ignored, result);
        Assert.Empty(store.GetOrderedSnapshot());
    }

    [Fact]
    public void UpdateUnknown_WithCompletePayload_CreatesRecoverableState()
    {
        var store = new DiscordMessageStore();

        store.Apply(DiscordMessageMutation.Update(TestMessageFactory.FullPatch(99)));

        Assert.Single(store.GetOrderedSnapshot());
    }

    [Fact]
    public void DeleteUnknown_IsIdempotentNoOp()
    {
        var store = new DiscordMessageStore();

        var result = store.Apply(DiscordMessageMutation.Delete("missing", "main"));

        Assert.Equal(MessageStoreMutationResult.Ignored, result);
        Assert.Empty(store.GetOrderedSnapshot());
    }

    [Fact]
    public void ChatRetention_KeepsTwentyOrFewer()
    {
        var store = new DiscordMessageStore(retentionLimit: 20);
        for (var id = 1; id <= 20; id++)
        {
            store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(id)));
        }

        Assert.Equal(20, store.Count);
    }

    [Fact]
    public void ChatRetention_TwentyFirstRemovesOldest()
    {
        var store = new DiscordMessageStore(retentionLimit: 20);
        for (var id = 1; id <= 21; id++)
        {
            store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(id)));
        }

        var snapshot = store.GetOrderedSnapshot();
        Assert.Equal(20, snapshot.Count);
        Assert.DoesNotContain(snapshot, message => message.MessageId == "1");
        Assert.Equal("2", snapshot[0].MessageId);
        Assert.Equal("21", snapshot[^1].MessageId);
    }

    [Fact]
    public void Update_DoesNotChangeRetentionOrderingWhenTimestampIsAbsent()
    {
        var store = new DiscordMessageStore(retentionLimit: 20);
        for (var id = 1; id <= 20; id++)
        {
            store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(id)));
        }

        store.Apply(DiscordMessageMutation.Update(TestMessageFactory.ContentPatch(1, "edited")));
        store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(21)));

        Assert.False(store.TryGet("1", out _));
        Assert.True(store.TryGet("2", out _));
        Assert.Equal(20, store.Count);
    }

    [Fact]
    public void Create_OrdersByCreatedAtThenSnowflakeRegardlessOfArrivalOrder()
    {
        var store = new DiscordMessageStore(retentionLimit: 20);
        var sameTime = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        store.Apply(DiscordMessageMutation.Create(
            TestMessageFactory.FullPatch(30) with
            {
                CreatedAt = OptionalValue<DateTimeOffset?>.From(sameTime.AddMinutes(1)),
            }));
        store.Apply(DiscordMessageMutation.Create(
            TestMessageFactory.FullPatch(20) with
            {
                CreatedAt = OptionalValue<DateTimeOffset?>.From(sameTime),
            }));
        store.Apply(DiscordMessageMutation.Create(
            TestMessageFactory.FullPatch(10) with
            {
                CreatedAt = OptionalValue<DateTimeOffset?>.From(sameTime),
            }));

        Assert.Equal(
            new[] { "10", "20", "30" },
            store.GetOrderedSnapshot().Select(message => message.MessageId));
    }

    [Fact]
    public void Update_WithDifferentCreatedAt_DoesNotReorderOrChangeCreationIdentity()
    {
        var store = new DiscordMessageStore(retentionLimit: 20);
        var firstCreatedAt = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var secondCreatedAt = firstCreatedAt.AddMinutes(1);
        store.Apply(DiscordMessageMutation.Create(
            TestMessageFactory.FullPatch(1) with
            {
                CreatedAt = OptionalValue<DateTimeOffset?>.From(firstCreatedAt),
            }));
        store.Apply(DiscordMessageMutation.Create(
            TestMessageFactory.FullPatch(2) with
            {
                CreatedAt = OptionalValue<DateTimeOffset?>.From(secondCreatedAt),
            }));

        store.Apply(DiscordMessageMutation.Update(
            TestMessageFactory.ContentPatch(1, "edited") with
            {
                CreatedAt = OptionalValue<DateTimeOffset?>.From(secondCreatedAt.AddHours(1)),
                EditedAt = OptionalValue<DateTimeOffset?>.From(secondCreatedAt.AddHours(1)),
            }));

        var snapshot = store.GetOrderedSnapshot();
        Assert.Equal(new[] { "1", "2" }, snapshot.Select(message => message.MessageId));
        Assert.Equal(firstCreatedAt, snapshot[0].CreatedAt);
        Assert.Equal("edited", snapshot[0].Content);
    }

    [Fact]
    public void Delete_RemovesOnlyTheExactMessageId()
    {
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(1)));
        store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(2)));

        store.Apply(DiscordMessageMutation.Delete("1", "main"));

        var remaining = Assert.Single(store.GetOrderedSnapshot());
        Assert.Equal("2", remaining.MessageId);
    }
}
