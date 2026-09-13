using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Rest;
using GachaOverlay.Core.Sales;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.CoreClient;
using LSOverlay.CoreReadProbe;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

if (args is ["--self-test"])
{
    await SafetyTests.RunAsync();
    return 0;
}
if (args is not ["--operator-readonly", var output] || !Path.IsPathFullyQualified(output)) return 2;
// Input comes over the operator's SSH -> container stdin pipe, not CLI/env files.
// No output includes the input, raw exception messages, URLs, or message contents.
var stage = "configuration";
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var input = new char[2049];
    var length = 0;
    int n;
    while ((n = await Console.In.ReadAsync(input.AsMemory(length), timeout.Token)) != 0)
    {
        length += n;
        if (length > 2048) throw new InvalidDataException();
    }
    var config = JsonSerializer.Deserialize<ProbeConfig>(input.AsSpan(0, length)) ?? throw new InvalidDataException();
    Array.Clear(input);
    if (config.GuildId == 0 || config.ApplicationId == 0 || config.ChatChannelId == 0 || string.IsNullOrWhiteSpace(config.BotToken)) throw new InvalidDataException();
    if (Directory.Exists(output)) throw new IOException();
    Directory.CreateDirectory(output);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    using var rest = new ReadOnlyRest(config.GuildId);
    using var client = new DiscordRestClient(new DiscordRestConfig
    {
        RestClientProvider = baseUrl => baseUrl == "https://discord.com/api/" || baseUrl == "https://discord.com/api/v10/"
            ? rest : throw new InvalidOperationException("Unexpected Discord API origin."),
        // Honor SDK pre-emptive bucket cooldowns. The transport still rejects
        // actual HTTP 429/errors, and the outer deadline prevents unbounded waits.
        DefaultRetryMode = RetryMode.RetryRatelimit,
        LogLevel = LogSeverity.Critical,
    });
    var options = new RequestOptions { CancelToken = timeout.Token, RetryMode = RetryMode.RetryRatelimit, Timeout = 10000 };
    stage = "bot-identity";
    await client.LoginAsync(TokenType.Bot, config.BotToken, validateToken: true);
    config = config with { BotToken = "" };
    var application = await client.GetApplicationInfoAsync(options);
    if (application.Id != config.ApplicationId) throw new InvalidDataException();
    stage = "guild";
    var guild = await client.GetGuildAsync(config.GuildId, options);
    stage = "channel-catalog";
    var channels = await guild.GetTextChannelsAsync(options);
    stage = "selected-channel";
    var channel = channels.Single(item => item.Id == config.ChatChannelId);
    // This is a bot-owner/operator perspective, NOT an authenticated Core user session.
    stage = "owner-membership";
    var ownerId = application.Team?.OwnerUserId ?? application.Owner.Id;
    var owner = await guild.GetUserAsync(ownerId, options) ?? throw new InvalidDataException();
    stage = "bot-membership";
    var bot = await guild.GetUserAsync(client.CurrentUser.Id, options) ?? throw new InvalidDataException();
    stage = "channel-permissions";
    foreach (var member in new[] { owner, bot })
    {
        var permissions = member.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.ReadMessageHistory) throw new InvalidDataException();
    }
    rest.SelectChannel(channel.Id);
    stage = "single-history-read";
    var messages = (await channel.GetMessagesAsync(20, options: options).FlattenAsync()).OrderBy(item => item.Timestamp).ThenBy(item => item.Id).ToArray();
    if (messages.Length == 0 || messages.Any(item => item.Channel.Id != channel.Id)) throw new InvalidDataException();
    stage = "canonical-projection";
    var authorSource = new RestAuthors(guild, options);
    var normalizer = new DiscordChatMessageNormalizer(new CanonicalRemoteAuthorResolver(authorSource));
    var canonical = await normalizer.NormalizeManyAsync(guild.Id, messages, timeout.Token);
    var normalized = canonical.Select(RemoteChatIngressAdapter.MapNormalizedMessage).ToArray();
    var snapshot = new CoreSemanticProjection(new OpaqueMetadata()).Capture("operator-capture-" + Guid.NewGuid().ToString("N"),
        1, owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), normalized,
        SalesQueueSnapshot.Empty, Array.Empty<HostPresenceSnapshot>());
    await File.WriteAllBytesAsync(Path.Combine(output, "chat.json"), JsonSerializer.SerializeToUtf8Bytes(snapshot, OverlayProtocolJson.Options), timeout.Token);
    var frames = CoreSnapshotWire.Encode(snapshot);
    var report = new
    {
        mode = "operator-readonly-one-shot",
        syntheticChat = false,
        authenticatedCoreSession = false,
        gatewayStarted = false,
        salesCaptured = false,
        sessionCaptured = false,
        mediaDownloaded = false,
        messages = snapshot.Chat.Count,
        requests = rest.Requests,
        customEmojiRuns = snapshot.Chat.Sum(item => item.Runs.Count(run => run.Kind == "CustomEmoji")),
        media = snapshot.Chat.Sum(item => item.Media.Count),
        replies = snapshot.Chat.Count(item => item.Reply is not null),
        roles = snapshot.Chat.Count(item => item.Author.Color is not null),
        wireFrames = frames.Count,
        maximumFrameBytes = frames.Max(item => item.Length),
    };
    await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(report), timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Core read-only probe stopped: stage={stage}; type={error.GetType().Name}; code={(error as ReadOnlyFailure)?.Code ?? "none"}. No retry performed.");
    return 1;
}

internal sealed record ProbeConfig(ulong GuildId, ulong ApplicationId, ulong ChatChannelId, string BotToken);

internal sealed class RestAuthors(RestGuild guild, RequestOptions options) : IRemoteGuildMemberSource, IRemoteGuildRoleStyleSource
{
    public async Task<RemoteGuildMemberResolution> ResolveAsync(ulong guildId, ulong authorId, CancellationToken cancellationToken)
    {
        if (guildId != guild.Id) throw new InvalidOperationException("Guild mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
        var user = await guild.GetUserAsync(authorId, options);
        return user is null ? new(RemoteGuildMemberResolutionStatus.NotFound) :
            new(RemoteGuildMemberResolutionStatus.Available, user.Nickname, user.RoleIds.ToArray(), ResolveRoleStyle(guildId, user.RoleIds.ToArray()));
    }

    public ChatAuthorStyle? ResolveRoleStyle(ulong guildId, IReadOnlyCollection<ulong>? roles) => guildId != guild.Id
        ? throw new InvalidOperationException("Guild mismatch.")
        : RemoteRoleStyleSelector.Select(roles, guild.Roles.Select(role =>
            new RemoteRoleDefinition(role.Id, role.Position, role.Colors.PrimaryColor.RawValue, role.Icon, role.Emoji?.Name)));
}

internal sealed class OpaqueMetadata : ICoreMediaReferences
{
    public string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl) =>
        "capture-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId + "\n" + kind + "\n" + identity))).ToLowerInvariant();
}
