using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Discord.Normalization;

public sealed class ExactGuildNicknameTests
{
    private readonly DiscordMessageNormalizer _normalizer = new(NullAppLogger.Instance);

    [Theory]
    [InlineData("DE-SSANTA")]
    [InlineData("-The First Star-")]
    [InlineData("--leading-and-trailing--")]
    [InlineData("internal-many-hyphens")]
    [InlineData("spaces  stay   exact")]
    [InlineData("[ABC] User.Name_☆")]
    [InlineData("_A_User_ **literal**")]
    public void Snapshot_MessageNickPreservesExactText(string nickname)
    {
        var response = Snapshot(nickname, globalName: "Fallback Name");

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main", "guild"));
        var store = new DiscordMessageStore();
        store.Apply(DiscordMessageMutation.Create(patch));
        var message = Assert.Single(store.GetOrderedSnapshot());

        Assert.Equal(nickname, message.AuthorGuildNickname);
        Assert.Equal(DiscordDisplayNameSource.GuildNickname, message.AuthorDisplayNameSource);
    }

    [Fact]
    public void Snapshot_MessageNickWinsOverGlobalName()
    {
        var patch = Assert.Single(_normalizer.NormalizeSnapshot(
            Snapshot("DE-SSANTA", "DE SANTA"),
            "main",
            "guild"));

        Assert.True(patch.AuthorGuildNickname.HasValue);
        Assert.Equal("DE-SSANTA", patch.AuthorGuildNickname.Value);
        Assert.Equal("DE SANTA", patch.AuthorDisplayName.Value);
    }

    [Fact]
    public void Snapshot_UsesGuildIdFromResponseBeforeHint()
    {
        var patch = Assert.Single(_normalizer.NormalizeSnapshot(
            Snapshot("Exact", "Global", "payload-guild"),
            "main",
            "hint-guild"));

        Assert.Equal("payload-guild", patch.GuildId.Value);
    }

    [Fact]
    public void LiveCreate_MessageNickIsExactGuildNickname()
    {
        var dispatch = Dispatch("MESSAGE_CREATE", "-The First Star-", "H");

        Assert.True(_normalizer.TryNormalizeDispatch(
            dispatch,
            out var mutation,
            out _,
            "guild"));

        Assert.Equal("-The First Star-", mutation!.Patch!.AuthorGuildNickname.Value);
        Assert.Equal("guild", mutation.Patch.GuildId.Value);
    }

    [Fact]
    public void LiveUpdate_MemberNicknameHasHighestPriority()
    {
        var dispatch = Parse("""
            {
              "evt": "MESSAGE_UPDATE",
              "data": {
                "channel_id": "main",
                "message": {
                  "id": "1",
                  "author": {
                    "id": "2",
                    "username": "user",
                    "global_name": "Global",
                    "display_name": "Computed"
                  },
                  "member": { "nick": "[ABC] Exact-Name" }
                }
              }
            }
            """);

        Assert.True(_normalizer.TryNormalizeDispatch(dispatch, out var mutation, out _, "guild"));

        Assert.Equal("[ABC] Exact-Name", mutation!.Patch!.AuthorGuildNickname.Value);
    }

    [Fact]
    public void Snapshot_UsesGlobalNameWhenExactGuildNameIsAbsent()
    {
        var response = Parse("""
            { "data": { "messages": [{
              "id": "1",
              "author": { "id": "2", "username": "user", "global_name": "Global Name" }
            }] } }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main", "guild"));

        Assert.False(patch.AuthorGuildNickname.HasValue);
        Assert.Equal("Global Name", patch.AuthorDisplayName.Value);
        Assert.Equal(DiscordDisplayNameSource.GlobalDisplayName, patch.AuthorDisplayNameSource.Value);
    }

    [Fact]
    public void Snapshot_UsesUsernameWhenAllDisplayNamesAreAbsent()
    {
        var response = Parse("""
            { "data": { "messages": [{
              "id": "1",
              "author": { "id": "2", "username": "raw-user" }
            }] } }
            """);

        var patch = Assert.Single(_normalizer.NormalizeSnapshot(response, "main", "guild"));

        Assert.Equal(DiscordDisplayNameSource.Username, patch.AuthorDisplayNameSource.Value);
        Assert.Equal("raw-user", patch.AuthorUsername.Value);
    }

    private static JsonElement Snapshot(
        string displayName,
        string globalName,
        string? guildId = null)
    {
        var payload = new
        {
            data = new
            {
                guild_id = guildId,
                messages = new[]
                {
                    new
                    {
                        id = "1",
                        channel_id = "main",
                        nick = displayName,
                        author = new
                        {
                            id = "2",
                            username = "user",
                            global_name = globalName,
                            display_name = displayName,
                        },
                        content = "hello",
                    },
                },
            },
        };
        return Parse(JsonSerializer.Serialize(payload));
    }

    private static JsonElement Dispatch(string eventName, string displayName, string globalName)
    {
        var payload = new
        {
            evt = eventName,
            data = new
            {
                channel_id = "main",
                message = new
                {
                    id = "1",
                    nick = displayName,
                    author = new
                    {
                        id = "2",
                        username = "user",
                        global_name = globalName,
                        display_name = displayName,
                    },
                },
            },
        };
        return Parse(JsonSerializer.Serialize(payload));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
