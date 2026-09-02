using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Discord;
using Discord.Net.Rest;
using Discord.Rest;
using Discord.WebSocket;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Pairing;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests.Backend;

public sealed class M9121PairCommandPermissionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"LSOverlay-M9121-{Guid.NewGuid():N}");
    private readonly DiscordSocketClient _socket;
    private readonly PairingHealth _health = new();
    private readonly ClientCredentialRegistry _credentials;
    private readonly PairingService _pairing;
    private readonly DiscordPairingCommand _command;
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public M9121PairCommandPermissionTests()
    {
        _socket = new DiscordSocketClient(new DiscordSocketConfig
        {
            RestClientProvider = _ => Stub<IRestClient>((method, _) =>
                method.Name is "Dispose" or "SetHeader" or "SetCancelToken"
                    ? null : throw new InvalidOperationException("Network forbidden.")),
        });
        var configuration = new BackendConfiguration(
            new BackendBotCredential("synthetic-test-token"), 123, Array.Empty<ulong>(), _directory);
        _credentials = new ClientCredentialRegistry(_directory, () => _now, expectedGuildId: 123);
        _pairing = new PairingService(_credentials, 123, () => _now);
        _command = new DiscordPairingCommand(_socket, configuration, _pairing, _health,
            new TransportMetrics(), NullLogger<DiscordPairingCommand>.Instance);
    }

    [Fact]
    public void Definition_HasNoPermissionBitsOrRoleOptionAndIsGuildOnly()
    {
        var definition = DiscordPairingCommand.Build();
        Assert.Equal("lsoverlay", definition.Name.Value);
        Assert.False(definition.DefaultMemberPermissions.IsSpecified);
        Assert.True(definition.IsDefaultPermission.Value);
        Assert.Equal(InteractionContextType.Guild, Assert.Single(definition.ContextTypes.Value));
        var pair = Assert.Single(definition.Options.Value);
        Assert.Equal("pair", pair.Name);
        Assert.Equal(ApplicationCommandOptionType.SubCommand, pair.Type);
        var code = Assert.Single(pair.Options);
        Assert.Equal("code", code.Name);
        Assert.Equal(ApplicationCommandOptionType.String, code.Type);
        Assert.True(code.IsRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscordNet3201_WireDistinguishesUnrestrictedNullFromAdminOnlyZero(bool restricted)
    {
        using var wire = new CommandWire();
        var definition = DiscordPairingCommand.Build();
        if (restricted)
        {
            definition.DefaultMemberPermissions = (GuildPermission)0;
        }

        await wire.UpsertAsync(definition);

        var payload = Assert.Single(wire.Payloads);
        Assert.True(payload.ContainsKey("default_member_permissions"));
        if (restricted)
        {
            Assert.Equal("0", payload["default_member_permissions"]!.ToString());
        }
        else
        {
            Assert.Null(payload["default_member_permissions"]);
        }
    }

    [Theory]
    [InlineData((ulong)0)]
    [InlineData((ulong)GuildPermission.Administrator)]
    [InlineData((ulong)GuildPermission.ManageGuild)]
    [InlineData((ulong)GuildPermission.ManageChannels)]
    [InlineData((ulong)GuildPermission.ManageRoles)]
    public async Task Reconcile_UpsertsOldRestrictedMetadataWithoutDeleteOrDuplicate(ulong oldPermissions)
    {
        using var wire = new CommandWire();
        var old = DiscordPairingCommand.Build();
        old.DefaultMemberPermissions = (GuildPermission)oldPermissions;
        var original = await wire.UpsertAsync(old);
        var guild = Stub<IGuild>((method, args) => method.Name switch
        {
            "get_Id" => 123UL,
            "CreateApplicationCommandAsync" => wire.UpsertAsync((ApplicationCommandProperties)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected guild operation: {method.Name}"),
        });

        await _command.ReconcileAsync(guild);
        await _command.ReconcileAsync(guild);

        Assert.Equal(PairingHealthState.Available, _health.State);
        Assert.Equal(3, wire.Payloads.Count);
        Assert.All(wire.Payloads.Skip(1), payload =>
        {
            Assert.True(payload.ContainsKey("default_member_permissions"));
            Assert.Null(payload["default_member_permissions"]);
            Assert.Equal("lsoverlay", payload["name"]!.ToString());
        });
        Assert.Equal(original.Id, wire.LastCommand!.Id);
        Assert.Single(wire.Registered);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(999UL)]
    public async Task Reconcile_RejectsMissingOrUnrelatedGuildWithoutAnyRegistration(ulong guildId)
    {
        var guild = guildId == 0 ? null : Stub<IGuild>((method, _) =>
            method.Name == "get_Id" ? guildId : throw new InvalidOperationException("Wrong guild API call."));
        await _command.ReconcileAsync(guild);
        Assert.Equal(PairingHealthState.Degraded, _health.State);
    }

    [Fact]
    public async Task Reconcile_RegistrationFailureIsNotReportedAvailable()
    {
        var guild = Stub<IGuild>((method, _) => method.Name switch
        {
            "get_Id" => 123UL,
            "CreateApplicationCommandAsync" => Task.FromException<IApplicationCommand>(new HttpRequestException()),
            _ => throw new InvalidOperationException(),
        });
        await _command.ReconcileAsync(guild);
        Assert.Equal(PairingHealthState.Degraded, _health.State);
    }

    [Fact]
    public async Task Reconcile_ConcurrentReadyCallbacksShareTheExistingGate()
    {
        var pending = new TaskCompletionSource<IApplicationCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var guild = Stub<IGuild>((method, _) =>
        {
            if (method.Name == "get_Id") return 123UL;
            if (method.Name != "CreateApplicationCommandAsync") throw new InvalidOperationException();
            calls++;
            return pending.Task;
        });
        var first = _command.ReconcileAsync(guild);
        try
        {
            await _command.ReconcileAsync(guild).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, calls);
        }
        finally
        {
            pending.TrySetResult(null!);
            await first;
        }
        Assert.Equal(PairingHealthState.Available, _health.State);
    }

    [Fact]
    public async Task OrdinaryMember_UsesInteractionIdentityAndClaimsOnlyItsBoundInstallation()
    {
        var installation = Guid.NewGuid();
        var created = _pairing.Create(installation);
        var input = new InteractionInput(123, 456, created.UserCode);

        await _command.HandleAsync(input.Command);

        Assert.Contains("approved", Assert.Single(input.Replies).Text, StringComparison.Ordinal);
        Assert.True(input.Replies[0].Ephemeral);
        Assert.Throws<UnauthorizedAccessException>(() => _pairing.Claim(created.PairingId, "wrong-secret"));
        var claimed = _pairing.Claim(created.PairingId, created.PairingClaimSecret);
        Assert.Equal(PairingState.Approved, claimed.State);
        Assert.Equal(new AuthenticatedClientIdentity(installation, 456, 123),
            _credentials.Authenticate(claimed.Credential!.AccessToken));
        Assert.Equal(PairingState.Consumed, _pairing.Claim(created.PairingId, created.PairingClaimSecret).State);
        Assert.DoesNotContain(created.UserCode, input.Replies[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(created.PairingClaimSecret, input.Replies[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(claimed.Credential.AccessToken, input.Replies[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("another-user")]
    public async Task OrdinaryMember_InvalidPairingIsRejectedPrivatelyWithoutNewState(string scenario)
    {
        var created = _pairing.Create(Guid.NewGuid());
        string? code = created.UserCode;
        if (scenario == "unknown") code = "not-a-pairing-code";
        if (scenario == "missing") code = null;
        if (scenario == "expired") _now += PairingService.PairingLifetime;
        if (scenario is "consumed" or "another-user")
        {
            _pairing.Approve(123, 789, false, code!);
            if (scenario == "consumed") _pairing.Claim(created.PairingId, created.PairingClaimSecret);
        }
        var credentialCount = _credentials.Count;
        var input = new InteractionInput(123, 456, code);

        await _command.HandleAsync(input.Command);

        Assert.Single(input.Replies);
        Assert.True(input.Replies[0].Ephemeral);
        Assert.DoesNotContain("pairing approved.", input.Replies[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, _pairing.Count);
        Assert.Equal(credentialCount, _credentials.Count);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(999UL)]
    public async Task DmAndUnrelatedGuild_CannotApproveAnOtherwiseValidCode(ulong guildId)
    {
        var created = _pairing.Create(Guid.NewGuid());
        var input = new InteractionInput(guildId == 0 ? null : guildId, 456, created.UserCode);
        await _command.HandleAsync(input.Command);
        Assert.Contains("configured server", Assert.Single(input.Replies).Text, StringComparison.Ordinal);
        Assert.True(input.Replies[0].Ephemeral);
        Assert.Equal(PairingState.Pending, _pairing.Claim(created.PairingId, created.PairingClaimSecret).State);
        Assert.Equal(0, _credentials.Count);
    }

    [Fact]
    public async Task BotCaller_CannotApproveAnOtherwiseValidCode()
    {
        var created = _pairing.Create(Guid.NewGuid());
        var input = new InteractionInput(123, 456, created.UserCode, isBot: true);
        await _command.HandleAsync(input.Command);
        Assert.Contains("invalid", Assert.Single(input.Replies).Text, StringComparison.Ordinal);
        Assert.True(input.Replies[0].Ephemeral);
        Assert.Equal(PairingState.Pending, _pairing.Claim(created.PairingId, created.PairingClaimSecret).State);
    }

    public void Dispose()
    {
        _socket.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var stub = DispatchProxy.Create<T, StrictProxy>();
        ((StrictProxy)(object)stub).Handler = invoke;
        return stub;
    }

    public class StrictProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed record InputOption(string Name, object Value, ApplicationCommandOptionType Type,
        IReadOnlyCollection<IApplicationCommandInteractionDataOption> Options) : IApplicationCommandInteractionDataOption;

    private sealed class InteractionInput
    {
        public ISlashCommandInteraction Command { get; }
        public List<(string Text, bool Ephemeral)> Replies { get; } = new();

        public InteractionInput(ulong? guildId, ulong userId, string? code, bool isBot = false)
        {
            var user = Stub<IGuildUser>((method, _) => method.Name switch
            {
                "get_Id" => userId,
                "get_IsBot" => isBot,
                _ => throw new InvalidOperationException($"Pairing must not inspect roles/permissions: {method.Name}"),
            });
            var options = code is null ? Array.Empty<IApplicationCommandInteractionDataOption>() :
                new IApplicationCommandInteractionDataOption[]
                {
                    new InputOption("pair", null!, ApplicationCommandOptionType.SubCommand,
                        new[] { new InputOption("code", code, ApplicationCommandOptionType.String,
                            Array.Empty<IApplicationCommandInteractionDataOption>()) }),
                };
            var data = Stub<IApplicationCommandInteractionData>((method, _) => method.Name switch
            {
                "get_Name" => "lsoverlay",
                "get_Options" => options,
                _ => throw new InvalidOperationException(method.Name),
            });
            Command = Stub<ISlashCommandInteraction>((method, args) =>
            {
                if (method.Name == "get_Data") return data;
                if (method.Name == "get_GuildId") return guildId;
                if (method.Name == "get_User") return user;
                if (method.Name != "RespondAsync") throw new InvalidOperationException(method.Name);
                var parameters = method.GetParameters();
                var ephemeralIndex = Array.FindIndex(parameters, item => item.Name == "ephemeral");
                Replies.Add(((string)args![0]!, (bool)args[ephemeralIndex]!));
                return Task.CompletedTask;
            });
        }
    }

    private sealed class CommandWire : IDisposable
    {
        private readonly DiscordRestClient _rest;
        public List<JsonObject> Payloads { get; } = new();
        public Dictionary<string, ulong> Registered { get; } = new();
        public IApplicationCommand? LastCommand { get; private set; }

        public CommandWire()
        {
            _rest = new DiscordRestClient(new DiscordRestConfig
            {
                RestClientProvider = _ => Stub<IRestClient>((method, args) =>
                {
                    if (method.Name is "Dispose" or "SetHeader" or "SetCancelToken") return null;
                    if (method.Name != "SendAsync") throw new InvalidOperationException(method.Name);
                    Assert.Equal("POST", args![0]);
                    var endpoint = Assert.IsType<string>(args[1]);
                    Assert.EndsWith("/guilds/123/commands", endpoint, StringComparison.Ordinal);
                    var payload = JsonNode.Parse(Assert.IsType<string>(args[2]))!.AsObject();
                    Payloads.Add(payload.DeepClone().AsObject());
                    var key = endpoint + "/" + payload["type"] + "/" + payload["name"];
                    if (!Registered.TryGetValue(key, out var id)) Registered[key] = id = (ulong)(42 + Registered.Count);
                    payload["id"] = id.ToString();
                    payload["application_id"] = "1";
                    payload["guild_id"] = "123";
                    payload["version"] = "1";
                    var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload.ToJsonString()));
                    return Task.FromResult(new RestResponse(HttpStatusCode.OK, new Dictionary<string, string>(), stream));
                }),
            });
        }

        public async Task<IApplicationCommand> UpsertAsync(ApplicationCommandProperties definition)
        {
            // Bypass only the SDK login-state check in this in-memory transport.
            // No token, socket, network provider or production Discord request is used.
            var options = new RequestOptions { RetryMode = RetryMode.AlwaysFail };
            typeof(RequestOptions).GetProperty("IgnoreState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(options, true);
            LastCommand = await _rest.CreateGuildCommand(definition, 123, options).WaitAsync(TimeSpan.FromSeconds(5));
            return LastCommand;
        }

        public void Dispose() => _rest.Dispose();
    }
}
