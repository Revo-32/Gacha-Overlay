using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales;

internal static class SalesTestFactory
{
    public static readonly DateTimeOffset Epoch =
        DateTimeOffset.Parse("2026-08-31T00:00:00Z");

    public static SalesStateEngine Engine(
        SalesProductCatalog? catalog = null,
        IGuildDisplayNameResolver? resolver = null)
    {
        resolver ??= new GuildDisplayNameResolver(clock: () => Epoch);
        resolver.SetAccountScope("account");
        return new SalesStateEngine(
            resolver,
            catalog,
            locale: "en",
            clock: () => Epoch);
    }

    public static NormalizedDiscordMessage Message(
        string id,
        string authorId = "author",
        int seconds = 0,
        string? nickname = "Seller",
        string globalName = "Global",
        string content = "sale",
        IReadOnlyList<DiscordCustomEmoji>? emojis = null,
        string guildId = "guild") => new(
        id,
        "sales",
        authorId,
        $"user-{authorId}",
        globalName,
        content,
        Epoch.AddSeconds(seconds),
        null,
        emojis ?? Array.Empty<DiscordCustomEmoji>(),
        Array.Empty<DiscordAttachmentMetadata>(),
        Array.Empty<DiscordEmbedMetadata>(),
        Array.Empty<DiscordMention>())
        {
            GuildId = guildId,
            AuthorGuildNickname = nickname,
            AuthorDisplayNameSource = nickname is null
            ? DiscordDisplayNameSource.GlobalDisplayName
            : DiscordDisplayNameSource.GuildNickname,
            AuthorGuildNicknameObservationSource = nickname is null
            ? DiscordDisplayNameSource.Unknown
            : DiscordDisplayNameSource.GuildNickname,
        };

    public static SalesObservationBatch Batch(
        long generation,
        bool trusted,
        SalesObservationStatus status,
        params SaleReactionObservation[] observations) => new(
        generation,
        Epoch.AddMinutes(generation),
        status,
        trusted,
        SalesObservationCompleteness.Full,
        observations);

    public static SaleReactionObservation Observation(
        string messageId,
        SaleReactionOutcome outcome,
        long generation,
        long? sourceRevision = null,
        bool trustedEvidence = true) => new(
        messageId,
        outcome,
        trustedEvidence,
        Epoch.AddMinutes(generation),
        generation,
        sourceRevision);

    public static void TrustPending(SalesStateEngine engine, string id, long generation = 1) =>
        engine.ApplyObservationBatch(Batch(
            generation,
            true,
            SalesObservationStatus.Live,
            Observation(id, SaleReactionOutcome.NotSold, generation)));

    public static void TrustSold(SalesStateEngine engine, string id, long generation = 1) =>
        engine.ApplyObservationBatch(Batch(
            generation,
            true,
            SalesObservationStatus.Live,
            Observation(id, SaleReactionOutcome.Sold, generation)));

    public static SalesProductCatalog Catalog(params SalesProductDefinition[] products) =>
        SalesProductCatalog.CreateValidated(new SalesProductCatalogDocument(
            SalesProductCatalogDocument.CurrentVersion,
            products));

    public static SalesProductDefinition Product(
        string productId,
        string emojiId,
        string emojiName,
        string english = "Product",
        string? korean = null,
        string? guildId = null) => new(
        productId,
        emojiId,
        emojiName,
        guildId,
        new Dictionary<string, string>
        {
            ["en"] = english,
            ["ko"] = korean ?? english,
        });
}
