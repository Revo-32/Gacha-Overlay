using System.Text.Json;
using Discord;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Core.Timers;
using GachaOverlay.Infrastructure.Settings;
using LSOverlay.Backend.Chat;
using LSOverlay.Protocol;
using GachaOverlay.Tests.Discord.Messages;

namespace GachaOverlay.Tests;

public sealed class M110FinalPolishTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void TimerPresets_AreTheApprovedExactDurations()
    {
        Assert.Equal(new[] { 12, 24, 48 }, GtaoTimerPresets.General);
        Assert.Equal(new[] { 40, 130 }, GtaoTimerPresets.Bunker);
        Assert.Equal(new[] { 90, 150 }, GtaoTimerPresets.Lsd);
    }

    [Fact]
    public void TimerSlots_AreIndependentAndRestartOnlyTheSelectedSlot()
    {
        var engine = new GtaoTimerEngine();
        engine.Start(GtaoTimerSlot.General, TimeSpan.FromMinutes(12), TimeSpan.Zero);
        engine.Start(GtaoTimerSlot.Bunker, TimeSpan.FromMinutes(40), TimeSpan.Zero);
        engine.Start(GtaoTimerSlot.Lsd, TimeSpan.FromMinutes(90), TimeSpan.Zero);
        engine.Start(GtaoTimerSlot.General, TimeSpan.FromMinutes(24), TimeSpan.FromMinutes(1));

        var values = engine.Read(TimeSpan.FromMinutes(2)).ToDictionary(item => item.Slot);
        Assert.Equal(TimeSpan.FromMinutes(23), values[GtaoTimerSlot.General].Remaining);
        Assert.Equal(TimeSpan.FromMinutes(38), values[GtaoTimerSlot.Bunker].Remaining);
        Assert.Equal(TimeSpan.FromMinutes(88), values[GtaoTimerSlot.Lsd].Remaining);
    }

    [Fact]
    public void TimerExpiry_IsBriefThenCollapsesWithoutPersistence()
    {
        var engine = new GtaoTimerEngine();
        engine.Start(GtaoTimerSlot.General, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.True(Assert.Single(engine.Read(TimeSpan.FromSeconds(1))).IsExpired);
        Assert.Empty(engine.Read(TimeSpan.FromSeconds(1) + GtaoTimerEngine.ExpiryEmphasisDuration));
        Assert.Empty(new GtaoTimerEngine().Read(TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(1, "00:01")]
    [InlineData(720, "12:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(5400, "1:30:00")]
    [InlineData(7800, "2:10:00")]
    [InlineData(65, "01:05")]
    [InlineData(7810, "2:10:10")]
    public void TimerFormatting_IsStable(int seconds, string expected) =>
        Assert.Equal(expected, GtaoTimerEngine.FormatRemaining(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(GtaoTimerSlot.General)]
    [InlineData(GtaoTimerSlot.Bunker)]
    [InlineData(GtaoTimerSlot.Lsd)]
    public void RestartingOneTimer_DoesNotResetEitherOtherSlot(GtaoTimerSlot restarted)
    {
        var durations = new Dictionary<GtaoTimerSlot, TimeSpan>
        {
            [GtaoTimerSlot.General] = TimeSpan.FromMinutes(12),
            [GtaoTimerSlot.Bunker] = TimeSpan.FromMinutes(40),
            [GtaoTimerSlot.Lsd] = TimeSpan.FromMinutes(90),
        };
        var engine = new GtaoTimerEngine();
        foreach (var pair in durations)
        {
            engine.Start(pair.Key, pair.Value, TimeSpan.Zero);
        }

        engine.Start(restarted, durations[restarted], TimeSpan.FromMinutes(2));
        var values = engine.Read(TimeSpan.FromMinutes(3)).ToDictionary(item => item.Slot);

        foreach (var slot in Enum.GetValues<GtaoTimerSlot>())
        {
            var expected = durations[slot] - TimeSpan.FromMinutes(slot == restarted ? 1 : 3);
            Assert.Equal(expected, values[slot].Remaining);
        }
    }

    [Fact]
    public void TimerHotkeys_DefaultToUnassigned()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(string.Empty, settings.GeneralTimerHotkey.Key);
        Assert.Equal(string.Empty, settings.BunkerTimerHotkey.Key);
        Assert.Equal(string.Empty, settings.LsdTimerHotkey.Key);
    }

    [Fact]
    public void TimerPresetsAndOptionalHotkeys_RoundTripWhileRuntimeStateDoesNotPersist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LSOverlay-M110-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "settings.json");

        try
        {
            var store = new JsonSettingsStore(path);
            var settings = AppSettings.CreateDefault() with
            {
                GeneralTimerMinutes = 48,
                BunkerTimerMinutes = 130,
                LsdTimerMinutes = 150,
                GeneralTimerHotkey = new HotkeySetting { Key = "F6" },
                BunkerTimerHotkey = new HotkeySetting { Key = "F7" },
                LsdTimerHotkey = new HotkeySetting { Key = "F8" },
            };

            Assert.True(store.Save(settings));
            var loaded = new JsonSettingsStore(path).Load();

            Assert.Equal(48, loaded.GeneralTimerMinutes);
            Assert.Equal(130, loaded.BunkerTimerMinutes);
            Assert.Equal(150, loaded.LsdTimerMinutes);
            Assert.Equal("F6", loaded.GeneralTimerHotkey.Key);
            Assert.Equal("F7", loaded.BunkerTimerHotkey.Key);
            Assert.Equal("F8", loaded.LsdTimerHotkey.Key);
            Assert.DoesNotContain("deadline", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(new GtaoTimerEngine().Read(TimeSpan.Zero));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ConsecutiveAuthorGrouping_UsesStableIdentityWithoutTimeThreshold()
    {
        Assert.Equal(new[] { true, false, true },
            ChatAuthorGrouping.ResolveHeaders(new[] { "1", "1", "2" }));
        Assert.Equal(new[] { true, false, false },
            ChatAuthorGrouping.ResolveHeaders(new[] { "1", "1", "1" }));
        Assert.Equal(new[] { true, true, true },
            ChatAuthorGrouping.ResolveHeaders(new[] { "1", "2", "1" }));
    }

    [Fact]
    public void ConsecutiveAuthorGrouping_RecomputesAfterDeleteOrReorder()
    {
        Assert.Equal(new[] { true, true, true }, ChatAuthorGrouping.ResolveHeaders(new[] { "1", "2", "1" }));
        Assert.Equal(new[] { true, false }, ChatAuthorGrouping.ResolveHeaders(new[] { "1", "1" }));
    }

    [Fact]
    public void RoleStyle_UsesHighestColoredAndHighestIconRolesIndependently()
    {
        var style = RemoteRoleStyleSelector.Select(
            [10, 20, 30],
            [
                new RemoteRoleDefinition(10, 1, 0x112233, null, null),
                new RemoteRoleDefinition(20, 5, 0, "hash", null),
                new RemoteRoleDefinition(30, 4, 0xabcdef, null, "⭐"),
            ]);

        Assert.NotNull(style);
        Assert.Equal((ulong)30, style.ColorRoleId);
        Assert.Equal((uint)0xabcdef, style.Color);
        Assert.Equal((ulong)20, style.IconRoleId);
        Assert.Equal("image", style.Icon?.Kind);
        Assert.Equal(
            "https://cdn.discordapp.com/role-icons/20/hash.png?size=32&quality=lossless",
            style.Icon?.Url);

        var tied = RemoteRoleStyleSelector.Select(
            [100, 200],
            [
                new RemoteRoleDefinition(100, 9, 0x111111, null, null),
                new RemoteRoleDefinition(200, 9, 0x222222, null, null),
            ]);
        Assert.Equal((ulong)200, tied?.ColorRoleId);
    }

    [Fact]
    public void RoleStyle_UnicodeIconAndNoStyleFallbackAreDeterministic()
    {
        var style = RemoteRoleStyleSelector.Select(
            [42],
            [new RemoteRoleDefinition(42, 3, 0, null, "🛡️")]);
        Assert.Equal("unicode", style?.Icon?.Kind);
        Assert.Equal("🛡️", style?.Icon?.Value);
        Assert.Null(RemoteRoleStyleSelector.Select([99],
            [new RemoteRoleDefinition(42, 3, 0, null, null)]));
    }

    [Fact]
    public void EveryTheme_HasExactWhiteNicknameFallback()
    {
        Assert.All(ColorThemeCatalog.All, theme =>
            Assert.Equal("#FFFFFF", theme.Colors[SemanticColorToken.ChatNickname]));
        Assert.Equal(System.Windows.Media.Colors.White,
            ((System.Windows.Media.SolidColorBrush)ColorThemeManager.CreateDiscordRoleBrush(null)).Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xab, 0xcd, 0xef),
            ((System.Windows.Media.SolidColorBrush)ColorThemeManager.CreateDiscordRoleBrush(0xabcdef)).Color);
    }

    [Fact]
    public void Protocol_OldPayloadWithoutDecorationsStillDeserializes()
    {
        const string json = """
        {"messageId":1,"guildId":2,"channelId":3,"messageType":"Default","rawMessageType":0,
        "author":{"userId":4,"username":"u","displayName":"U","guildNickname":null,"isBot":false,"isWebhook":false},
        "content":"hello","createdAt":"2026-01-01T00:00:00Z","editedAt":null,"isPinned":false,
        "isTts":false,"mentionedEveryone":false,"flags":0,"customEmojis":[],"attachments":[],
        "embeds":[],"mentions":[],"stickers":[],"forwardedSnapshots":[],"reference":null,"components":[],"poll":null}
        """;
        var message = JsonSerializer.Deserialize<ChatMessage>(json, OverlayProtocolJson.Options);
        Assert.NotNull(message);
        Assert.Null(message.Author?.RoleStyle);
        Assert.Empty(message.Reactions);
    }

    [Fact]
    public void Protocol_NewDecorationsRoundTripAndRemainReadOnlyData()
    {
        var original = Message() with
        {
            Author = new ChatAuthor(4, "u", "U", null, false, false)
            {
                RoleStyle = new ChatAuthorStyle(7, 0xabcdef, 8,
                    new ChatRoleIcon("unicode", "⭐")),
            },
            Reactions =
            [
                new ChatReaction(new ChatEmoji(null, "👍", false), 2),
                new ChatReaction(new ChatEmoji(10, "party", false), 1),
            ],
        };
        var json = JsonSerializer.Serialize(original, OverlayProtocolJson.Options);
        var copy = JsonSerializer.Deserialize<ChatMessage>(json, OverlayProtocolJson.Options);
        Assert.Equal(original.Author?.RoleStyle, copy?.Author?.RoleStyle);
        Assert.Equal(original.Reactions, copy?.Reactions);
        Assert.DoesNotContain("write", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protocol_NewPayloadIsIgnoredSafelyByRc1EquivalentJsonShape()
    {
        var decorated = Message() with
        {
            Author = new ChatAuthor(4, "u", "U", null, false, false)
            {
                RoleStyle = new ChatAuthorStyle(7, 0xabcdef, 8,
                    new ChatRoleIcon("unicode", "⭐")),
            },
            Reactions = [new ChatReaction(new ChatEmoji(null, "👍", false), 2)],
        };

        var legacy = JsonSerializer.Deserialize<LegacyChatMessage>(
            JsonSerializer.Serialize(decorated, OverlayProtocolJson.Options),
            OverlayProtocolJson.Options);

        Assert.NotNull(legacy);
        Assert.Equal((ulong)1, legacy.MessageId);
        Assert.Equal((ulong)4, legacy.Author?.UserId);
        Assert.Equal("hello", legacy.Content);
    }

    [Fact]
    public void ReactionNormalization_FiltersZeroAndOrdersUnicodeBeforeCustom()
    {
        var reactions = new Dictionary<IEmote, ReactionMetadata>
        {
            [new Emote(20, "party", false)] = Reaction(2),
            [new Emoji("👍")] = Reaction(3),
            [new Emoji("👎")] = Reaction(0),
        };

        var mapped = DiscordChatMessageNormalizer.MapReactions(reactions);
        Assert.Equal(2, mapped.Count);
        Assert.Null(mapped[0].Emoji.Id);
        Assert.Equal("👍", mapped[0].Emoji.Name);
        Assert.Equal((ulong)20, mapped[1].Emoji.Id);
        Assert.Equal(2, mapped[1].Count);
    }

    [Fact]
    public void ReactionState_SurvivesBootstrapAndTracksAddRemoveAndClearRefreshes()
    {
        var pipeline = new DiscordMessagePipeline();
        var targets = new DiscordTargetChannels("guild", "Guild", "main", "Main", "sales", "Sales");
        Assert.True(pipeline.StartBootstrap(1, targets));
        Assert.True(pipeline.CompleteBootstrap(
            1,
            [WithReactions(1,
                new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "👍", false), 4),
                new DiscordMessageReaction(new DiscordCustomEmoji("20", "party", false), 2))],
            []));
        Assert.Equal(2, Assert.Single(pipeline.Current.MainChat).Reactions.Count);

        Assert.True(pipeline.ReceiveLive(1, DiscordMessageMutation.Update(
            ReactionPatch(1,
                new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "👍", false), 5),
                new DiscordMessageReaction(new DiscordCustomEmoji("20", "party", false), 2),
                new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "🔥", false), 1)))));
        Assert.Equal(3, Assert.Single(pipeline.Current.MainChat).Reactions.Count);

        Assert.True(pipeline.ReceiveLive(1, DiscordMessageMutation.Update(
            ReactionPatch(1,
                new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "👍", false), 4),
                new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "🔥", false), 1)))));
        Assert.DoesNotContain(Assert.Single(pipeline.Current.MainChat).Reactions,
            reaction => reaction.Emoji.EmojiId == "20");

        Assert.True(pipeline.ReceiveLive(1, DiscordMessageMutation.Update(ReactionPatch(1))));
        Assert.Empty(Assert.Single(pipeline.Current.MainChat).Reactions);
        Assert.True(pipeline.ReceiveLive(1, DiscordMessageMutation.Update(ReactionPatch(999,
            new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "❌", false), 1)))));
        Assert.Single(pipeline.Current.MainChat);
        Assert.Empty(Assert.Single(pipeline.Current.MainChat).Reactions);
    }

    [Fact]
    public void ReactionState_IsReleasedWhenMessageIsDeletedOrLeavesRecentTwenty()
    {
        var store = new DiscordMessageStore(retentionLimit: 20);
        store.Apply(DiscordMessageMutation.Create(WithReactions(1,
            new DiscordMessageReaction(new DiscordCustomEmoji(string.Empty, "👍", false), 1))));
        for (var id = 2; id <= 21; id++)
        {
            store.Apply(DiscordMessageMutation.Create(TestMessageFactory.FullPatch(id)));
        }

        Assert.False(store.TryGet("1", out _));
        Assert.Equal(MessageStoreMutationResult.Removed,
            store.Apply(DiscordMessageMutation.Delete("21", "main")));
        Assert.False(store.TryGet("21", out _));
    }

    [Fact]
    public void CustomReactionPresentation_UsesNameFallbackWithoutRawSnowflakeMarkup()
    {
        var viewModel = new ChatReactionViewModel(
            new DiscordMessageReaction(new DiscordCustomEmoji("20", "party", false), 2));

        Assert.Equal(":party:", viewModel.Text);
        Assert.True(viewModel.ShowText);
        Assert.DoesNotContain("20", viewModel.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<:", viewModel.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Gateway_AllReactionMutationsRefreshCanonicalChatWithoutWriteBack()
    {
        var gateway = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "LSOverlay.Backend", "Discord", "DiscordGatewayAdapter.cs"));
        Assert.Contains("ReactionAdded", gateway);
        Assert.Contains("ReactionRemoved", gateway);
        Assert.Contains("ReactionsCleared", gateway);
        Assert.Contains("ReactionsRemovedForEmote", gateway);
        Assert.Contains("_remoteChat.ReceiveUpdateAsync(channel.Id, messageId)", gateway);
        var chatView = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "GachaOverlay.App", "Presentation", "ChatMessageView.xaml"));
        Assert.DoesNotContain("Command=\"{Binding", chatView.AsSpan(
            chatView.IndexOf("ReactionTemplate", StringComparison.Ordinal), 900).ToString());
    }

    [Fact]
    public void StaticUi_OmitsLegacyTimerClusterAndKeepsReadOnlyReactionsAndRoleIcon()
    {
        var hud = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "GachaOverlay.App", "Presentation", "HudWindow.xaml"));
        var chat = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "GachaOverlay.App", "Presentation", "ChatMessageView.xaml"));
        Assert.DoesNotContain("Timers.Items", hud);
        Assert.Contains("ReactionTemplate", chat);
        Assert.Contains("IsHitTestVisible=\"False\"", chat);
        Assert.Contains("RoleIconImage", chat);
        Assert.Contains("ShowAuthorHeader", chat);
    }

    [Fact]
    public void ChosunGulim_IsBundledUnmodifiedWithOfficialNotice()
    {
        var font = Path.Combine(RepositoryRoot,
            "src", "GachaOverlay.App", "Assets", "Fonts", "ChosunGu.TTF");
        var notice = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "GachaOverlay.App", "Assets", "Fonts", "ThirdPartyNotices", "NOTICE-Fonts.txt"));
        Assert.True(File.Exists(font));
        Assert.Equal("D6FAB61A754BC7BFD0E5E4259B3AB4156F0386A0ADA0679329A31D130F38DA81",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(font))));
        Assert.Contains("https://event.chosun.com/100/100font.html", notice);
        Assert.Equal("조선굴림체", ChatSettings.ResolveTypography(ChatFontPreset.ChosunGulim).DisplayName);
        var resolved = new ChatTypographyResolver(
                NullAppLogger.Instance,
                new WpfChatFontCatalog(Path.GetDirectoryName(font)))
            .Resolve(ChatFontPreset.ChosunGulim);
        Assert.False(resolved.Nickname.IsFallback);
        Assert.False(resolved.Message.IsFallback);
    }

    private static ChatMessage Message() => new(
        1, 2, 3, "Default", 0, null, "hello", DateTimeOffset.UnixEpoch, null,
        false, false, false, 0, [], [], [], [], [], [], null, [], null);

    private static ReactionMetadata Reaction(int count)
    {
        object boxed = default(ReactionMetadata);
        typeof(ReactionMetadata).GetField(
            "<ReactionCount>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(boxed, count);
        return (ReactionMetadata)boxed;
    }

    private static DiscordMessagePatch WithReactions(
        long messageId,
        params DiscordMessageReaction[] reactions) =>
        TestMessageFactory.FullPatch(messageId) with
        {
            Reactions = OptionalValue<IReadOnlyList<DiscordMessageReaction>>.From(reactions),
        };

    private static DiscordMessagePatch ReactionPatch(
        long messageId,
        params DiscordMessageReaction[] reactions) => new(messageId.ToString())
        {
            ChannelId = OptionalValue<string>.From("main"),
            Reactions = OptionalValue<IReadOnlyList<DiscordMessageReaction>>.From(reactions),
        };

    private sealed record LegacyChatMessage(
        ulong MessageId,
        LegacyChatAuthor? Author,
        string Content);

    private sealed record LegacyChatAuthor(
        ulong UserId,
        string Username,
        string? DisplayName,
        string? GuildNickname,
        bool IsBot,
        bool IsWebhook);
}
