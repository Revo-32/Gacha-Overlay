using System.Reflection;
using Discord;
using Discord.Rest;
using LSOverlay.Backend.Chat;
using Newtonsoft.Json;

namespace GachaOverlay.Tests.Backend;

public sealed class M9141RestMentionTests
{
    [Theory]
    [InlineData("Display Name", "Display Name")]
    [InlineData(null, "account_name")]
    [InlineData("", "account_name")]
    public async Task Real_rest_message_preserves_mention_names_without_extra_member_requests(string? globalName, string expected)
    {
        using var client = new DiscordRestClient(); // No Login, Start or network provider calls.
        var json = JsonConvert.SerializeObject(new
        {
            id = "123",
            channel_id = "456",
            type = 0,
            content = "hello <@789>",
            timestamp = "2026-09-03T00:00:00Z",
            edited_timestamp = (string?)null,
            author = new { id = "999", username = "author", discriminator = "0" },
            mentions = new[] { new { id = "789", username = "account_name", global_name = globalName, discriminator = "0" } },
            mention_roles = Array.Empty<string>(),
            attachments = Array.Empty<object>(),
            embeds = Array.Empty<object>(),
            pinned = false,
            tts = false,
            mention_everyone = false,
        });
        // Exercise the installed SDK's actual REST model conversion, not a fake
        // IMessage whose mention behavior could conceal this Socket/REST mismatch.
        var modelType = typeof(RestMessage).Assembly.GetType("Discord.API.Message", throwOnError: true)!;
        var contractType = typeof(RestMessage).Assembly.GetType("Discord.Net.Converters.DiscordContractResolver", throwOnError: true)!;
        var model = JsonConvert.DeserializeObject(json, modelType, new JsonSerializerSettings
        {
            ContractResolver = (Newtonsoft.Json.Serialization.IContractResolver)Activator.CreateInstance(contractType, nonPublic: true)!,
        })!;
        var channel = DispatchProxy.Create<IMessageChannel, ChannelProxy>();
        var author = DispatchProxy.Create<IUser, UserProxy>();
        var message = (RestMessage)typeof(RestMessage).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new[] { client, (object)channel, author, model })!;
        Assert.IsType<RestUserMessage>(message);
        Assert.Single(message.MentionedUsers);
        var members = new MemberSource();
        var normalizer = new DiscordChatMessageNormalizer(new CanonicalRemoteAuthorResolver(members));
        var normalized = await normalizer.NormalizeAsync(1, message);
        var mention = Assert.Single(normalized.Mentions);
        Assert.Equal(789UL, mention.Id);
        Assert.Equal("user", mention.Kind);
        Assert.Equal(expected, mention.DisplayName);
        Assert.Equal(1, members.Requests); // Existing author resolution only.
        Assert.Equal("hello <@789>", normalized.Content);
    }

    public class ChannelProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "get_Id" ? 456UL : throw new InvalidOperationException(method?.Name);
    }

    public class UserProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Id" => 999UL,
            "get_Username" => "author",
            "get_GlobalName" => null,
            "get_IsBot" or "get_IsWebhook" => false,
            _ => throw new InvalidOperationException(method?.Name),
        };
    }

    private sealed class MemberSource : IRemoteGuildMemberSource
    {
        public int Requests { get; private set; }
        public Task<RemoteGuildMemberResolution> ResolveAsync(ulong guildId, ulong authorId, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new RemoteGuildMemberResolution(RemoteGuildMemberResolutionStatus.NotFound));
        }
    }
}
