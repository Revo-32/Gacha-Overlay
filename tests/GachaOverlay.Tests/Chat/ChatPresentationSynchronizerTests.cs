using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Chat;

public sealed class ChatPresentationSynchronizerTests
{
    [Fact]
    public void Snapshot_HydratesSelfMentionWithoutPulse()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var state = State(1, Message("1", "hello <@42>", mentions: new[]
        {
            new DiscordMention("42", "Me"),
        }));

        var change = Assert.Single(synchronizer.Synchronize(state, "42"));

        Assert.Equal(ChatPresentationChangeKind.SnapshotAdd, change.Kind);
        Assert.True(change.Message!.HasSelfMention);
        Assert.False(change.RequestMentionPulse);
        Assert.True(Assert.Single(change.Message.Tokens.Where(x => x.Kind == ChatTokenKind.Mention)).IsSelfMention);
    }

    [Fact]
    public void LiveCreate_PulsesSelfMentionAndDuplicateStateDoesNothing()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        synchronizer.Synchronize(State(1, Message("1", "first")), "42");
        var live = State(
            1,
            Message("1", "first"),
            Message("2", "hello <@42>", mentions: new[] { new DiscordMention("42", "Me") }));

        var change = Assert.Single(synchronizer.Synchronize(live, "42"));

        Assert.Equal(ChatPresentationChangeKind.Add, change.Kind);
        Assert.True(change.RequestMentionPulse);
        Assert.Empty(synchronizer.Synchronize(live, "42"));
    }

    [Fact]
    public void Update_PulsesOnlyOnTransitionToSelfMentionAndKeepsIndex()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        synchronizer.Synchronize(
            State(1, Message("1", "first"), Message("2", "ordinary")),
            "42");
        var updated = State(
            1,
            Message("1", "first"),
            Message("2", "edited <@42>", mentions: new[] { new DiscordMention("42", "Me") }));

        var change = Assert.Single(synchronizer.Synchronize(updated, "42"));

        Assert.Equal(ChatPresentationChangeKind.Update, change.Kind);
        Assert.Equal(1, change.Index);
        Assert.True(change.RequestMentionPulse);
        Assert.Empty(synchronizer.Synchronize(updated, "42"));
    }

    [Fact]
    public void Update_RetainingSelfMentionDoesNotPulseAgain()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        synchronizer.Synchronize(
            State(
                1,
                Message(
                    "1",
                    "before <@42>",
                    mentions: new[] { new DiscordMention("42", "Me") })),
            "42");
        var updated = State(
            1,
            Message(
                "1",
                "after <@42>",
                mentions: new[] { new DiscordMention("42", "Me") }));

        var change = Assert.Single(synchronizer.Synchronize(updated, "42"));

        Assert.Equal(ChatPresentationChangeKind.Update, change.Kind);
        Assert.False(change.RequestMentionPulse);
    }

    [Fact]
    public void Delete_ProducesRemovalAndCannotResurrectFromUnchangedState()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        synchronizer.Synchronize(State(1, Message("1", "first"), Message("2", "second")), null);
        var deleted = State(1, Message("1", "first"));

        var change = Assert.Single(synchronizer.Synchronize(deleted, null));

        Assert.Equal(ChatPresentationChangeKind.Remove, change.Kind);
        Assert.Equal("2", change.MessageId);
        Assert.Empty(synchronizer.Synchronize(deleted, null));
    }

    [Fact]
    public void NewGeneration_RefreshesIdentityWithoutSnapshotPulse()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var message = Message(
            "1",
            "hello <@42>",
            mentions: new[] { new DiscordMention("42", "Me") });
        synchronizer.Synchronize(State(1, message), "42");

        var change = Assert.Single(synchronizer.Synchronize(State(2, message), "42"));

        Assert.Equal(ChatPresentationChangeKind.Update, change.Kind);
        Assert.Equal(2, change.Message!.Generation);
        Assert.False(change.RequestMentionPulse);
    }

    [Fact]
    public void EmojiAndMedia_ProjectFallbackAnimationAndHttpsOnly()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var message = Message(
            "1",
            "go <a:dance:99>",
            emojis: new[] { new DiscordCustomEmoji("99", "dance", true) },
            attachments: new[]
            {
                new DiscordAttachmentMetadata("1", "safe.png", "https://cdn.test/safe.png", null, 10, 20, 20, "image/png"),
                new DiscordAttachmentMetadata("2", "unsafe.png", "http://cdn.test/unsafe.png", null, 10, 20, 20, "image/png"),
            });

        var projected = Assert.Single(synchronizer.Synchronize(State(1, message), null)).Message!;

        var emoji = Assert.Single(projected.Tokens.Where(x => x.Kind == ChatTokenKind.CustomEmoji));
        Assert.Equal(":dance:", emoji.Text);
        Assert.True(emoji.IsAnimatedEmoji);
        Assert.Equal("https://cdn.test/safe.png", Assert.Single(projected.Media).Url);
    }

    [Fact]
    public void AuthorName_PrefersGuildNicknameThenDisplayNameThenUsername()
    {
        var message = Message("1", "hello") with { AuthorGuildNickname = "Server Nick" };

        Assert.Equal("Server Nick", ChatPresentationSynchronizer.ResolveAuthorName(message));
        Assert.Equal(
            "Display",
            ChatPresentationSynchronizer.ResolveAuthorName(
                message with { AuthorGuildNickname = " " }));
        Assert.Equal(
            "user",
            ChatPresentationSynchronizer.ResolveAuthorName(
                message with
                {
                    AuthorGuildNickname = null,
                    AuthorDisplayName = null,
                }));
        Assert.Equal(
            "Unknown",
            ChatPresentationSynchronizer.ResolveAuthorName(
                message with
                {
                    AuthorGuildNickname = null,
                    AuthorDisplayName = null,
                    AuthorUsername = "",
                }));
    }

    [Fact]
    public void OrdinaryMention_DoesNotRequestSelfMentionPulse()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        synchronizer.Synchronize(State(1, Message("1", "first")), "42");
        var live = State(
            1,
            Message("1", "first"),
            Message(
                "2",
                "hello <@77>",
                mentions: new[] { new DiscordMention("77", "Other") }));

        var change = Assert.Single(synchronizer.Synchronize(live, "42"));

        Assert.False(change.Message!.HasSelfMention);
        Assert.False(change.RequestMentionPulse);
    }

    [Fact]
    public void SelfIdentity_UsesUserIdEvenWhenDisplayNameIsUnrelated()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var message = Message(
            "1",
            "hello <@42>",
            mentions: new[] { new DiscordMention("42", "Completely Different Nick") });

        var projected = Assert.Single(
            synchronizer.Synchronize(State(1, message), "42")).Message!;

        var mention = Assert.Single(projected.Tokens.Where(x => x.Kind == ChatTokenKind.Mention));
        Assert.Equal("42", mention.Identity);
        Assert.True(mention.IsSelfMention);
    }

    [Fact]
    public void StickerMetadata_ProjectsWithoutChangingMessageIdentity()
    {
        var synchronizer = new ChatPresentationSynchronizer();
        var message = Message("1", "") with
        {
            Stickers = new[]
            {
                new DiscordStickerMetadata("900", "Wave", 2, null),
            },
        };

        var projected = Assert.Single(synchronizer.Synchronize(State(1, message), null)).Message!;

        var sticker = Assert.Single(projected.Stickers);
        Assert.Equal("1", projected.MessageId);
        Assert.Equal("900", sticker.StickerId);
        Assert.Equal("Wave", sticker.Name);
        Assert.Equal(2, sticker.FormatType);
    }

    private static DiscordMessageState State(long generation, params NormalizedDiscordMessage[] messages) =>
        new(generation, false, messages, Array.Empty<NormalizedDiscordMessage>());

    private static NormalizedDiscordMessage Message(
        string id,
        string content,
        IReadOnlyList<DiscordMention>? mentions = null,
        IReadOnlyList<DiscordCustomEmoji>? emojis = null,
        IReadOnlyList<DiscordAttachmentMetadata>? attachments = null) => new(
            id,
            "main",
            "author",
            "user",
            "Display",
            content,
            DateTimeOffset.Parse("2026-08-30T01:00:00Z").AddSeconds(int.Parse(id)),
            null,
            emojis ?? Array.Empty<DiscordCustomEmoji>(),
            attachments ?? Array.Empty<DiscordAttachmentMetadata>(),
            Array.Empty<DiscordEmbedMetadata>(),
            mentions ?? Array.Empty<DiscordMention>());
}
