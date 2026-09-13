using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;

namespace LSOverlay.CoreFixtureHost;

internal static class ChatFixtureData
{
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, first, count, revision) in new[] { ("chat20.json", 1, 20, 1L), ("chat21.json", 1, 21, 2L), ("retained20.json", 81, 20, 3L) })
            File.WriteAllBytes(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(Create(first, count, revision), OverlayProtocolJson.Options));
    }

    public static CoreSnapshot Create(int first, int count, long revision)
    {
        var created = DateTimeOffset.Parse("2026-09-13T10:00:00Z");
        var messages = Enumerable.Range(first, count).Select(index =>
        {
            var author = index % 4 is 0 or 1 ? "22" : "33";
            var content = (index % 6) switch
            {
                0 => "한국어 줄바꿈 검증입니다. 창의 폭과 글꼴을 변경해도 문장과 스크롤 위치가 안정적으로 유지되어야 합니다. 가나다라마바사 ABC 123.",
                1 => "직접 멘션 <@77> · 다른 사용자 <@88> · 원문 유지 **강조 문법**",
                2 => "Unicode 이모지 🙂 🚗 🎉 ❤️ 가족 👨‍👩‍👧‍👦\n두 번째 줄 · 결합 문자 e\u0301 · 日本語",
                3 => "링크 원문 https://example.com/test?q=1 · 코드 `hello` · <script>실행하지 않음</script>",
                4 => "답글과 반응을 확인합니다.",
                _ => "커스텀 이모지 <:sample:12345> (미디어 파이프라인 검증은 M4)"
            };
            return new NormalizedDiscordMessage(index.ToString(), "synthetic-chat", author, "synthetic-author", author == "22" ? "검증용 작성자" : "다른 작성자",
                content, created.AddSeconds(index * 30), null, Array.Empty<DiscordCustomEmoji>(), Array.Empty<DiscordAttachmentMetadata>(),
                Array.Empty<DiscordEmbedMetadata>(), content.Contains("<@77>", StringComparison.Ordinal)
                    ? new[] { new DiscordMention("77", "나"), new DiscordMention("88", "친구") } : Array.Empty<DiscordMention>())
            {
                Attachments = index % 7 == 0 ? new[] { new DiscordAttachmentMetadata("attachment", "검증용-안내.txt", "https://cdn.discordapp.com/synthetic/guide.txt", null, 24, null, null, "text/plain") } : Array.Empty<DiscordAttachmentMetadata>(),
                AuthorStyle = new DiscordAuthorStyle("role", author == "22" ? 0xffcf00U : 0x80d8bbU, "role", new DiscordRoleIcon("unicode", "★")),
                Reactions = index % 6 == 4 ? new[] { new DiscordMessageReaction(new DiscordCustomEmoji("", "👍", false), 3) } : Array.Empty<DiscordMessageReaction>(),
                RemoteMetadata = index % 6 == 4 ? new DiscordRemoteMessageMetadata("default", 0, 0, false, false, false, false, false,
                    new DiscordReplyMetadata("reply", "g", "synthetic-chat", "previous") { ResolvedAuthorId = "77", ResolvedAuthorName = "나", ResolvedContent = "답글 원문입니다. 한글도 정상 표시" },
                    Array.Empty<DiscordForwardSnapshotMetadata>(), Array.Empty<DiscordComponentMetadata>(), null) : null
            };
        }).ToArray();
        return new CoreSemanticProjection(new MetadataOnlyMedia()).Capture("synthetic-chat-v1", revision, "77", messages, SalesQueueSnapshot.Empty,
            new[] { new HostPresenceSnapshot(1, HostPresenceState.GtaOnline, 12, 30, created) });
    }

    private sealed class MetadataOnlyMedia : ICoreMediaReferences
    {
        public string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl) => "synthetic-" + kind + "-" + messageId;
    }
}
