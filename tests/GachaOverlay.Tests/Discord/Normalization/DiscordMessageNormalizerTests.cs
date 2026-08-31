using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Discord.Normalization;

public sealed class DiscordMessageNormalizerTests
{
    private readonly DiscordMessageNormalizer _normalizer = new(NullAppLogger.Instance);

    [Fact]
    public void Snapshot_PreservesSnowflakePrecisionAuthorAndCustomEmoji()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": 18446744073709551615,
                  "channel_id": "200000000000000001",
                  "author": {
                    "id": "300000000000000001",
                    "username": "rev",
                    "global_name": "Rev"
                  },
                  "content": "hello <:product_name:400000000000000001>",
                  "content_parsed": [{
                    "type": "customEmoji",
                    "id": "400000000000000001",
                    "name": "product_name"
                  }],
                  "timestamp": "2026-08-30T10:00:00.000Z"
                }]
              }
            }
            """);

        var patches = _normalizer.NormalizeSnapshot(response, "fallback-channel");
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patches.Single()));
        var message = store.GetOrderedSnapshot().Single();

        Assert.Equal("18446744073709551615", message.MessageId);
        Assert.Equal("300000000000000001", message.AuthorId);
        Assert.Equal("Rev", message.AuthorDisplayName);
        var emoji = Assert.Single(message.CustomEmojis);
        Assert.Equal("400000000000000001", emoji.EmojiId);
        Assert.Equal("product_name", emoji.Name);
    }

    [Fact]
    public void Snapshot_MalformedOptionalFieldsDoNotCrashPipeline()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": 99 },
                  "content": { "unexpected": true },
                  "timestamp": 123,
                  "attachments": "malformed",
                  "embeds": null
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        var result = store.Apply(DiscordMessageMutation.Create(patch));

        Assert.Equal(MessageStoreMutationResult.Applied, result);
        var message = Assert.Single(store.GetOrderedSnapshot());
        Assert.Empty(message.Attachments);
        Assert.Empty(message.Embeds);
    }

    [Fact]
    public void Snapshot_PreservesAttachmentAndEmbedDiagnostics()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "image",
                  "attachments": [{
                    "id": "10",
                    "filename": "image.png",
                    "url": "https://cdn.example/image.png",
                    "width": 128,
                    "height": 64,
                    "size": 2048,
                    "content_type": "image/png"
                  }],
                  "embeds": [{
                    "type": "image",
                    "url": "https://example.test/page",
                    "image": { "url": "https://cdn.example/embed.png" }
                  }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));
        var message = Assert.Single(store.GetOrderedSnapshot());

        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("image.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal("https://cdn.example/embed.png", Assert.Single(message.Embeds).ImageUrl);
    }

    [Fact]
    public void Snapshot_PreservesMentionIdentityForSelfDetection()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "hello <@42>",
                  "mentions": [{
                    "id": "42",
                    "username": "target",
                    "global_name": "Target User"
                  }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));

        var mention = Assert.Single(Assert.Single(store.GetOrderedSnapshot()).Mentions);
        Assert.Equal("42", mention.UserId);
        Assert.Equal("Target User", mention.DisplayName);
    }

    [Fact]
    public void Snapshot_PreservesGuildNicknameAndStickerItems()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": {
                    "id": "2",
                    "username": "account-name",
                    "global_name": "Display Name"
                  },
                  "member": { "nick": "Server Nick" },
                  "content": "",
                  "sticker_items": [{
                    "id": "900",
                    "name": "Wave",
                    "format_type": 2
                  }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));
        var message = Assert.Single(store.GetOrderedSnapshot());

        Assert.Equal("Server Nick", message.AuthorGuildNickname);
        var sticker = Assert.Single(message.Stickers);
        Assert.Equal("900", sticker.StickerId);
        Assert.Equal("Wave", sticker.Name);
        Assert.Equal(2, sticker.FormatType);
    }

    [Fact]
    public void Snapshot_PreservesRepeatedEmojiOccurrencesForProductQuantity()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "<:bunker:100> <:plate:200> <:bunker:100>"
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "sales"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));

        Assert.Equal(
            new[] { "100", "200", "100" },
            Assert.Single(store.GetOrderedSnapshot()).CustomEmojis.Select(emoji => emoji.EmojiId));
    }

    [Fact]
    public void Snapshot_NameOnlySticker_RemainsAvailableForFallbackRendering()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "",
                  "sticker_items": [{ "sticker_name": "External Wave", "formatType": 3 }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));
        var sticker = Assert.Single(Assert.Single(store.GetOrderedSnapshot()).Stickers);

        Assert.Equal(string.Empty, sticker.StickerId);
        Assert.Equal("External Wave", sticker.Name);
        Assert.Equal(3, sticker.FormatType);
    }

    [Fact]
    public void Snapshot_LegacyStickersFieldAndDifferentGuildMetadata_AreNotFiltered()
    {
        var response = Parse("""
            {
              "data": {
                "guild_id": "current-guild",
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "",
                  "stickers": [{
                    "id": "external-900",
                    "name": "External Wave",
                    "format_type": 4,
                    "guild_id": "different-guild"
                  }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));

        var sticker = Assert.Single(Assert.Single(store.GetOrderedSnapshot()).Stickers);
        Assert.Equal("external-900", sticker.StickerId);
        Assert.Equal(4, sticker.FormatType);
    }

    [Fact]
    public void Snapshot_EmptyStickerItemsDoesNotHidePopulatedLegacyStickers()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "",
                  "sticker_items": [],
                  "stickers": [{
                    "id": "legacy-900",
                    "name": "Legacy Wave",
                    "format_type": 2
                  }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));

        var sticker = Assert.Single(Assert.Single(store.GetOrderedSnapshot()).Stickers);
        Assert.Equal("legacy-900", sticker.StickerId);
        Assert.Equal("Legacy Wave", sticker.Name);
        Assert.Equal(2, sticker.FormatType);
    }

    [Fact]
    public void Snapshot_MergesPartialRpcStickerFieldsAndCamelCaseParsedSticker()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "",
                  "sticker_items": [{ "id": "900", "name": "Wave" }],
                  "stickers": [{ "id": "900", "format_type": 4 }],
                  "content_parsed": [{
                    "type": "sticker",
                    "stickerId": "901",
                    "stickerName": "Parsed Wave",
                    "formatType": 1
                  }]
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));

        var stickers = Assert.Single(store.GetOrderedSnapshot()).Stickers;
        Assert.Collection(
            stickers,
            sticker =>
            {
                Assert.Equal("900", sticker.StickerId);
                Assert.Equal("Wave", sticker.Name);
                Assert.Equal(4, sticker.FormatType);
            },
            sticker =>
            {
                Assert.Equal("901", sticker.StickerId);
                Assert.Equal("Parsed Wave", sticker.Name);
                Assert.Equal(1, sticker.FormatType);
            });
    }

    [Fact]
    public void Snapshot_OpaqueEmptyWithoutPositiveStickerEvidenceUsesNeutralFallback()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [{
                  "id": "1",
                  "blocked": false,
                  "author": { "id": "2", "username": "rev" },
                  "content": "",
                  "embeds": [],
                  "attachments": [],
                  "type": 0
                }]
              }
            }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));

        var message = Assert.Single(store.GetOrderedSnapshot());
        Assert.Empty(message.Stickers);
        Assert.Equal(DiscordMessageFallbackKind.Message, message.FallbackKind);
        Assert.Null(message.Forward);
    }

    [Fact]
    public void Snapshot_BlockedOrNonDefaultBlankMessageDoesNotBecomeStickerFallback()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [
                  {
                    "id": "blocked",
                    "blocked": true,
                    "author": { "id": "2", "username": "rev" },
                    "content": "",
                    "embeds": [],
                    "attachments": [],
                    "type": 0
                  },
                  {
                    "id": "system",
                    "author": { "id": "2", "username": "rev" },
                    "content": "",
                    "embeds": [],
                    "attachments": [],
                    "type": 19
                  }
                ]
              }
            }
            """);

        var patches = _normalizer.NormalizeSnapshot(response, "main");
        var store = new DiscordMessageStore();
        foreach (var patch in patches)
        {
            store.Apply(DiscordMessageMutation.Create(patch));
        }

        Assert.All(store.GetOrderedSnapshot(), message => Assert.Empty(message.Stickers));
    }

    [Fact]
    public void StickerMessageUpdate_ReplacesOldStickerMetadata()
    {
        var store = new DiscordMessageStore();
        var create = Assert.Single(_normalizer.NormalizeSnapshot(Parse("""
            { "data": { "messages": [{
              "id": "1", "author": { "id": "2", "username": "rev" },
              "content": "", "sticker_items": [{ "id": "old", "name": "Old", "format_type": 1 }]
            }] } }
            """), "main"));
        store.Apply(DiscordMessageMutation.Create(create));
        Assert.True(_normalizer.TryNormalizeDispatch(Parse("""
            { "evt": "MESSAGE_UPDATE", "data": { "message": {
              "id": "1", "sticker_items": [{ "id": "new", "name": "New", "format_type": 2 }]
            } } }
            """), out var mutation, out _));

        store.Apply(mutation!);

        Assert.Equal("new", Assert.Single(Assert.Single(store.GetOrderedSnapshot()).Stickers).StickerId);
    }

    [Fact]
    public void StickerMessageDelete_RemovesPresentationSource()
    {
        var store = new DiscordMessageStore();
        var create = Assert.Single(_normalizer.NormalizeSnapshot(Parse("""
            { "data": { "messages": [{
              "id": "1", "author": { "id": "2", "username": "rev" },
              "content": "", "sticker_items": [{ "id": "900", "name": "Wave", "format_type": 2 }]
            }] } }
            """), "main"));
        store.Apply(DiscordMessageMutation.Create(create));
        Assert.True(_normalizer.TryNormalizeDispatch(Parse("""
            { "evt": "MESSAGE_DELETE", "data": { "message_id": "1" } }
            """), out var mutation, out _));

        store.Apply(mutation!);

        Assert.Empty(store.GetOrderedSnapshot());
    }

    [Theory]
    [InlineData("MESSAGE_CREATE", DiscordMessageMutationKind.Create)]
    [InlineData("MESSAGE_UPDATE", DiscordMessageMutationKind.Update)]
    public void Dispatch_NormalizesCreateAndUpdate(
        string eventName,
        DiscordMessageMutationKind expectedKind)
    {
        var dispatch = Parse($$"""
            {
              "cmd": "DISPATCH",
              "evt": "{{eventName}}",
              "data": {
                "channel_id": "main",
                "message": {
                  "id": "1",
                  "author": { "id": "2", "username": "rev" },
                  "content": "hello"
                }
              }
            }
            """);

        var normalized = _normalizer.TryNormalizeDispatch(
            dispatch,
            out var mutation,
            out var parsedEvent);

        Assert.True(normalized);
        Assert.Equal(eventName, parsedEvent);
        Assert.Equal(expectedKind, mutation!.Kind);
        Assert.Equal("main", mutation.ChannelId);
    }

    [Fact]
    public void Dispatch_NormalizesDeleteWithoutFullMessage()
    {
        var dispatch = Parse("""
            {
              "cmd": "DISPATCH",
              "evt": "MESSAGE_DELETE",
              "data": {
                "channel_id": "sales",
                "message": { "id": "999" }
              }
            }
            """);

        var normalized = _normalizer.TryNormalizeDispatch(
            dispatch,
            out var mutation,
            out _);

        Assert.True(normalized);
        Assert.Equal(DiscordMessageMutationKind.Delete, mutation!.Kind);
        Assert.Equal("999", mutation.MessageId);
        Assert.Equal("sales", mutation.ChannelId);
    }

    [Fact]
    public void Snapshot_MissingRequiredMessageIdSkipsOnlyMalformedItem()
    {
        var response = Parse("""
            {
              "data": {
                "messages": [
                  { "content": "missing id" },
                  {
                    "id": "2",
                    "author": { "id": "3", "username": "valid" },
                    "content": "valid"
                  }
                ]
              }
            }
            """);

        var patches = _normalizer.NormalizeSnapshot(response, "main");

        Assert.Equal("2", Assert.Single(patches).MessageId);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
