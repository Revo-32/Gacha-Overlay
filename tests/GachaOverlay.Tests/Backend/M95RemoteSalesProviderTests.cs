using System.Text.Json;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Tests.Sales;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Sales;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;

public sealed class M95RemoteSalesProviderTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Theory]
    [InlineData(1451583544295034940UL, "wrong", true, false)]
    [InlineData(1418284521337651321UL, "wrong", false, true)]
    [InlineData(null, "SOLD", true, false)]
    [InlineData(null, "closed", false, true)]
    [InlineData(1UL, "SOLD", false, false)]
    [InlineData(2UL, "closed", false, false)]
    [InlineData(null, "sold", false, false)]
    [InlineData(null, "Closed", false, false)]
    public void CompletionMarkers_UseIdFirstAndExactNameFallback(
        ulong? id,
        string name,
        bool sold,
        bool closed)
    {
        Assert.Equal(sold, RemoteSalesPolicy.IsSoldMarker(id, name));
        Assert.Equal(closed, RemoteSalesPolicy.IsClosedMarker(id, name));
    }

    [Fact]
    public void CompletionObservation_UsesOrSemantics()
    {
        Assert.True(Observation(1, sold: true, closed: false).IsSold);
        Assert.True(Observation(1, sold: false, closed: true).IsSold);
        Assert.True(Observation(1, sold: true, closed: true).IsSold);
        Assert.False(Observation(1, sold: false, closed: false).IsSold);
    }

    [Fact]
    public void Protocol_RoundTripsAdditiveSalesFields()
    {
        var message = new StreamServerMessage(
            OverlayTransportProtocol.Version,
            OverlayTransportProtocol.SalesCompletionEvidence,
            ChannelId: RemoteSalesPolicy.ProductionSalesChannelId,
            SalesGeneration: "sales-generation",
            SalesLatestSequence: 42,
            SalesEvent: new SalesMutationEnvelope(
                OverlayTransportProtocol.Version,
                "sales-generation",
                42,
                OverlayTransportProtocol.SalesCompletionEvidence,
                RemoteSalesPolicy.ProductionSalesChannelId,
                123,
                null,
                Observation(123, sold: true, closed: false)));

        var json = JsonSerializer.Serialize(message, OverlayProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<StreamServerMessage>(
            json,
            OverlayProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(42, restored.SalesLatestSequence);
        Assert.True(restored.SalesEvent!.CompletionObservation!.IsSold);
        Assert.Null(restored.ChatEvent);
    }

    [Fact]
    public void Registry_BootstrapJournalsRacingCreateWithoutDuplication()
    {
        var registry = Registry();
        var capture = registry.Activate();
        var raced = Message(2);
        Assert.True(registry.PublishUpsert(
            OverlayTransportProtocol.SalesMessageCreate,
            raced,
            Observation(2, sold: false, closed: false)));

        var completed = registry.CompleteBootstrap(
            capture,
            new[] { Message(1) },
            new[] { Observation(1, sold: false, closed: false) });

        Assert.Equal(SalesResumeDisposition.Resumable, completed.Disposition);
        Assert.Equal(new ulong[] { 1, 2 }, completed.Messages.Select(item => item.MessageId));
        Assert.Equal(2, completed.Messages.Select(item => item.MessageId).Distinct().Count());
        Assert.Equal(completed.Messages.Count, completed.Observations.Count);
    }

    [Fact]
    public void Registry_BootstrapRetainsLatestThirtySessionSalesPosts()
    {
        Assert.Equal(30, AuthoritativeSalesWindow.Size);
        Assert.Equal(
            AuthoritativeSalesWindow.Size,
            ActiveSalesStreamRegistry.AuthoritativeWindowSize);
        var registry = Registry();
        var messages = Enumerable.Range(1, 35)
            .Select(index => Message((ulong)index))
            .ToArray();
        var observations = Enumerable.Range(1, 35)
            .Select(index => Observation((ulong)index, sold: false, closed: false))
            .ToArray();

        var completed = registry.CompleteBootstrap(
            registry.Activate(),
            messages,
            observations);

        Assert.Equal(30, completed.Messages.Count);
        Assert.Equal(30, completed.Observations.Count);
        Assert.Equal(6UL, completed.Messages[0].MessageId);
        Assert.Equal(35UL, completed.Messages[^1].MessageId);
        Assert.Equal(
            completed.Messages.Select(item => item.MessageId).OrderBy(id => id),
            completed.Observations.Select(item => item.MessageId).OrderBy(id => id));
        Assert.Equal(
            SalesBootstrapCoverage.Complete,
            RemoteSalesService.DetermineBootstrapCoverage(completed.Messages.Count));
    }

    [Fact]
    public async Task Registry_ReplayIsOrderedAndSeparatelySequenced()
    {
        var registry = Registry();
        var capture = registry.Activate();
        var completed = registry.CompleteBootstrap(
            capture,
            new[] { Message(1) },
            new[] { Observation(1, sold: false, closed: false) });
        registry.PublishEvidence(Observation(1, sold: true, closed: false));
        registry.PublishEvidence(Observation(1, sold: true, closed: true));

        var resume = registry.PrepareResume(completed.Generation, completed.LatestSequence);
        await using var subscription = Assert.IsType<SalesStreamSubscription>(
            resume.Subscription);
        Assert.Equal(new long[] { 1, 2 }, subscription.Replay.Select(item => item.Sequence));
        Assert.All(subscription.Replay, item => Assert.Equal(
            completed.Generation,
            item.Generation));
    }

    [Fact]
    public void Registry_WrongGenerationAndFutureSequenceRequireResync()
    {
        var registry = Registry();
        var completed = registry.CompleteBootstrap(
            registry.Activate(),
            Array.Empty<ChatMessage>(),
            Array.Empty<SalesCompletionObservation>());

        Assert.Equal(
            SalesResumeDisposition.WrongGeneration,
            registry.PrepareResume("wrong", 0).Disposition);
        Assert.Equal(
            SalesResumeDisposition.FutureSequence,
            registry.PrepareResume(completed.Generation, 1).Disposition);
    }

    [Fact]
    public void Registry_IsBoundedAndExpiresOldReplayHistory()
    {
        var registry = Registry();
        var completed = registry.CompleteBootstrap(
            registry.Activate(),
            new[] { Message(1) },
            new[] { Observation(1, sold: false, closed: false) });
        for (var index = 0; index < ActiveSalesStreamRegistry.JournalCapacity + 1; index++)
        {
            registry.PublishEvidence(Observation(
                1,
                sold: index % 2 == 0,
                closed: false));
        }

        Assert.Equal(
            SalesResumeDisposition.HistoryExpired,
            registry.PrepareResume(completed.Generation, 0).Disposition);
    }

    [Fact]
    public void BackendConfiguration_DefaultsToFixedProductionSalesChannel()
    {
        var configuration = Configuration();

        Assert.Equal(RemoteSalesPolicy.ProductionSalesChannelId, configuration.SalesChannelId);
        Assert.DoesNotContain(
            RemoteSalesPolicy.ProductionSalesChannelId.ToString(),
            configuration.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackendConfiguration_AllowsServerSideSalesChannelOverride()
    {
        var values = new Dictionary<string, string?>
        {
            [BackendEnvironmentVariables.BotToken] = "secret",
            [BackendEnvironmentVariables.GuildId] = "10",
            [BackendEnvironmentVariables.TrackedHostIds] = string.Empty,
            [BackendEnvironmentVariables.SalesChannelId] = "20",
        };

        var result = BackendConfigurationLoader.Load(name => values.GetValueOrDefault(name));

        Assert.True(result.IsValid);
        Assert.Equal(20UL, result.Configuration!.SalesChannelId);
    }

    [Fact]
    public void RemoteProvider_AdvertisesM97ConstrainedWriteBack()
    {
        var remote = OverlayProviderCatalog.LsOverlayRemote;

        Assert.True(remote.Supports(
            OverlayDataCapabilities.SalesMessages |
            OverlayDataCapabilities.SalesCompletionEvidence));
        Assert.True(remote.Supports(OverlayDataCapabilities.SalesReactionWriteBack));
    }

    [Fact]
    public void TrustedCompleteAbsenceCanReopenButPartialAbsenceCannot()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Apply(engine, 1, SaleReactionOutcome.Sold, trusted: true);
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);

        Apply(engine, 2, SaleReactionOutcome.NotSold, trusted: false);
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);

        Apply(engine, 3, SaleReactionOutcome.NotSold, trusted: true);
        Assert.Equal(SaleDomainState.Pending, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void RepeatedRemoteSoldEvidenceDoesNotDuplicateDomainTransition()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Apply(engine, 1, SaleReactionOutcome.Sold, trusted: true);
        var revision = engine.Current.Revision;

        Apply(engine, 2, SaleReactionOutcome.Sold, trusted: true);

        Assert.Equal(revision, engine.Current.Revision);
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void ValidationHelper_IsDedicatedAndContainsRemoteSalesChecklist()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-m95-local.ps1"));
        var shared = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-m94-local.ps1"));

        Assert.Contains("ValidationMilestone 'M9.5'", helper, StringComparison.Ordinal);
        Assert.Contains("Sales Tracking = ON", shared, StringComparison.Ordinal);
        Assert.Contains("Remote Sales becomes Live", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendSource_DoesNotEnumerateReactionUsersOrWriteToDiscord()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "LSOverlay.Backend",
            "Sales",
            "RemoteSalesService.cs"));

        Assert.DoesNotContain("GetReactionUsers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetReactionsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddReactionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveReactionAsync", source, StringComparison.Ordinal);
        Assert.Contains("message.Reactions", source, StringComparison.Ordinal);
    }

    private static ActiveSalesStreamRegistry Registry() => new(Configuration());

    private static BackendConfiguration Configuration() => new(
        new BackendBotCredential("secret"),
        10,
        Array.Empty<ulong>(),
        Path.Combine(Path.GetTempPath(), "m95-tests"));

    private static SalesCompletionObservation Observation(
        ulong messageId,
        bool sold,
        bool closed) => new(
            messageId,
            sold,
            closed,
            SalesEvidenceCoverage.Complete,
            Epoch);

    private static ChatMessage Message(ulong messageId) => new(
        messageId,
        10,
        RemoteSalesPolicy.ProductionSalesChannelId,
        "Default",
        0,
        new ChatAuthor(20, "seller", "Seller", "Seller", false, false),
        $"sale-{messageId}",
        Epoch.AddSeconds(messageId),
        null,
        false,
        false,
        false,
        0,
        Array.Empty<ChatEmoji>(),
        Array.Empty<ChatAttachment>(),
        Array.Empty<ChatEmbed>(),
        Array.Empty<ChatMention>(),
        Array.Empty<ChatSticker>(),
        Array.Empty<ChatForwardSnapshot>(),
        null,
        Array.Empty<ChatComponent>(),
        null);

    private static void Apply(
        SalesStateEngine engine,
        long generation,
        SaleReactionOutcome outcome,
        bool trusted)
    {
        engine.ApplyObservationBatch(new SalesObservationBatch(
            generation,
            Epoch.AddSeconds(generation),
            trusted ? SalesObservationStatus.Live : SalesObservationStatus.Partial,
            trusted,
            trusted ? SalesObservationCompleteness.Full : SalesObservationCompleteness.Partial,
            new[]
            {
                new SaleReactionObservation(
                    "1",
                    outcome,
                    trusted,
                    Epoch.AddSeconds(generation),
                    generation),
            },
            trusted ? SalesCoverageState.Complete : SalesCoverageState.Partial));
    }
}
