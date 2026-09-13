using System.Security.Cryptography;
using System.Text;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;

namespace LSOverlay.CoreFixtureHost;

internal static class CoreFixtureData
{
    public static CoreSnapshot Create(string generation, long revision, bool large = false)
    {
        var viewer = large ? "18446744073709551614" : "77";
        var created = DateTimeOffset.Parse("2026-09-13T00:00:00Z");
        var content = large ? string.Concat(Enumerable.Repeat("한국어🙂 원문 그대로 ", 3000)) : "네이티브 연결 검증 <@77> <:sample:12345>";
        var message = new NormalizedDiscordMessage("18446744073709551613", "fixture-chat", "22", "fixture-author",
            "검증용 작성자", content, created, null, Array.Empty<DiscordCustomEmoji>(),
            Array.Empty<DiscordAttachmentMetadata>(), Array.Empty<DiscordEmbedMetadata>(), new[] { new DiscordMention("77", "나") });
        var resolver = new GuildDisplayNameResolver(); resolver.SetAccountScope("isolated-core-fixture");
        var engine = new SalesStateEngine(resolver, locale: "ko-KR");
        engine.SetAuthenticatedUser(viewer);
        // Real domain engine supplies the canonical queue (empty in the M2 fixture).
        engine.ApplySourceSnapshot(Array.Empty<NormalizedDiscordMessage>());
        return new CoreSemanticProjection(new MetadataOnlyMedia()).Capture(generation, revision, viewer,
            new[] { message }, engine.Current, new[] { new HostPresenceSnapshot(1, HostPresenceState.GtaOnline, 12, 30, created) });
    }

    private sealed class MetadataOnlyMedia : ICoreMediaReferences
    {
        public string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId + "|" + kind + "|" + identity))).ToLowerInvariant();
    }
}
