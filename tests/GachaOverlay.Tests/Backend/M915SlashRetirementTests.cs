using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Migrations;

namespace GachaOverlay.Tests.Backend;

public sealed class M915SlashRetirementTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactOwnedGuildCommandIsDeletedAndVerifiedOnce(bool guildField)
    {
        using var source = new CommandApi();
        var legacy = Command();
        if (!guildField) legacy.Remove("guild_id");
        source.Commands.Add(legacy);
        source.Commands.Add(Command("unrelated", "2"));
        source.Commands.Add(Command("lsoverlay", "3", application: "999"));
        source.Commands.Add(Command("lsoverlay", "4", guild: "999"));
        source.Commands.Add(Command("lsoverlay", "5", type: 2));
        using var migration = Create(source);
        await migration.RunAsync(default);
        Assert.True(migration.Completed);
        Assert.Equal(new[] { "/api/v10/applications/77/guilds/123/commands/1" }, source.Deleted);
        Assert.Equal(4, source.Commands.Count);
        var requests = source.Requests;
        await migration.RunAsync(default);
        Assert.Equal(requests, source.Requests);
    }

    [Fact]
    public async Task AbsentCommandNeedsNoDeleteAndFreshProcessCanReconcileAgain()
    {
        using var source = new CommandApi();
        using (var migration = Create(source)) { await migration.RunAsync(default); Assert.True(migration.Completed); }
        using (var restarted = Create(source)) { await restarted.RunAsync(default); Assert.True(restarted.Completed); }
        Assert.Empty(source.Deleted);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(403)]
    public async Task ApiFailureLeavesRetrySafeStateAndDoesNotPersistCompletion(int status)
    {
        using var source = new CommandApi { Failure = (HttpStatusCode)status };
        source.Commands.Add(Command());
        using var migration = Create(source);
        await Assert.ThrowsAsync<HttpRequestException>(() => migration.RunAsync(default));
        Assert.False(migration.Completed);
        Assert.Empty(source.Deleted);
        source.Failure = null;
        await migration.RunAsync(default);
        Assert.True(migration.Completed);
        Assert.Single(source.Deleted);
    }

    [Fact]
    public async Task DeleteNotFoundRaceIsHarmlessButAbsenceMustStillBeVerified()
    {
        using var source = new CommandApi { DeleteStatus = HttpStatusCode.NotFound };
        source.Commands.Add(Command());
        using var migration = Create(source);
        await migration.RunAsync(default);
        Assert.True(migration.Completed);
    }

    [Fact]
    public async Task SuccessfulDeleteResponseWithCommandStillPresentDoesNotMarkComplete()
    {
        using var source = new CommandApi { KeepAfterDelete = true };
        source.Commands.Add(Command());
        using var migration = Create(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => migration.RunAsync(default));
        Assert.False(migration.Completed);
    }

    [Theory]
    [InlineData("extra-subcommand")]
    [InlineData("renamed-option")]
    [InlineData("wrong-type")]
    [InlineData("optional-code")]
    public async Task ChangedLegacyShapeIsNotDeleted(string change)
    {
        using var source = new CommandApi();
        var command = Command();
        var options = command["options"]!.AsArray();
        switch (change)
        {
            case "extra-subcommand": options.Add(new JsonObject { ["name"] = "new-feature", ["type"] = 1 }); break;
            case "renamed-option": options[0]!["options"]![0]!["name"] = "different"; break;
            case "wrong-type": options[0]!["options"]![0]!["type"] = 4; break;
            case "optional-code": options[0]!["options"]![0]!["required"] = false; break;
        }
        source.Commands.Add(command);
        using var migration = Create(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => migration.RunAsync(default));
        Assert.False(migration.Completed);
        Assert.Empty(source.Deleted);
    }

    [Fact]
    public async Task CancellationNeverMarksCompletion()
    {
        using var source = new CommandApi();
        using var migration = Create(source);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.RunAsync(cancelled.Token));
        Assert.False(migration.Completed);
        Assert.Empty(source.Deleted);
    }

    private static SlashPairingRetirementMigration Create(CommandApi source) => new(
        new BackendConfiguration(new BackendBotCredential("synthetic-bot"), 123, new ulong[] { 99 }), source);

    private static JsonObject Command(string name = "lsoverlay", string id = "1", string application = "77", string guild = "123", int type = 1) => new()
    {
        ["id"] = id,
        ["application_id"] = application,
        ["guild_id"] = guild,
        ["type"] = type,
        ["name"] = name,
        ["options"] = new JsonArray(new JsonObject
        {
            ["name"] = "pair",
            ["type"] = 1,
            ["options"] = new JsonArray(new JsonObject { ["name"] = "code", ["type"] = 3, ["required"] = true }),
        }),
    };

    private sealed class CommandApi : HttpMessageHandler
    {
        public JsonArray Commands { get; } = new();
        public List<string> Deleted { get; } = new();
        public int Requests { get; private set; }
        public HttpStatusCode? Failure { get; set; }
        public HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.NoContent;
        public bool KeepAfterDelete { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("discord.com", request.RequestUri.Host);
            Assert.Equal("Bot", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-bot", request.Headers.Authorization.Parameter);
            Assert.True(request.Method == HttpMethod.Get || request.Method == HttpMethod.Delete);
            if (Failure is { } status) return Task.FromResult(new HttpResponseMessage(status));
            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/v10/oauth2/applications/@me") return Json("{\"id\":\"77\"}");
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal("/api/v10/applications/77/guilds/123/commands/1", path);
                Deleted.Add(path);
                if (!KeepAfterDelete)
                    Commands.Remove(Commands.Single(command => command!["id"]!.GetValue<string>() == "1"));
                return Task.FromResult(new HttpResponseMessage(DeleteStatus));
            }
            Assert.Equal("/api/v10/applications/77/guilds/123/commands", path);
            return Json(Commands.ToJsonString());
        }

        private static Task<HttpResponseMessage> Json(string text) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") });
    }
}
