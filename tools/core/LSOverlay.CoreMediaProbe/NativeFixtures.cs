using System.Text.Json;
using System.Text.Json.Nodes;
using LSOverlay.CoreMedia;

namespace LSOverlay.CoreMediaProbe;
internal static class NativeFixtures
{
    public static async Task Export(string directory)
    {
        Directory.CreateDirectory(directory);
        using var cache = new DerivativeCache(Path.Combine(directory, "cache"));
        var source = Fixtures.Gif(0, 120);
        var animation = await cache.GetOrCreateAsync(new MemoryStream(source), new(240, 240));
        var emoji = await cache.GetOrCreateAsync(new MemoryStream(source), new(32, 32));
        var image = await cache.GetOrCreateAsync(new MemoryStream(Fixtures.Png(99, 480, 280)), new(240, 140));
        var mappings = new[] { new { id = "fixture-animation", file = animation + ".lscm", width = 240, height = 240 }, new { id = "fixture-emoji", file = emoji + ".lscm", width = 32, height = 32 }, new { id = "fixture-emoji-alias", file = emoji + ".lscm", width = 32, height = 32 }, new { id = "fixture-image", file = image + ".lscm", width = 240, height = 140 } };
        foreach (var item in mappings.DistinctBy(item => item.file)) File.Copy(Path.Combine(cache.Root, item.file), Path.Combine(directory, item.file));
        File.WriteAllText(Path.Combine(directory, "media.json"), JsonSerializer.Serialize(mappings));
        var rows = new JsonArray();
        for (var i = 0; i < 20; i++)
        {
            var last = i == 19;
            rows.Add(new JsonObject
            {
                ["id"] = "fixture-" + i,
                ["presentationHash"] = i.ToString("x64"),
                ["attention"] = "Normal",
                ["showAuthorHeader"] = true,
                ["author"] = new JsonObject { ["id"] = "synthetic", ["displayName"] = "미디어 검증 " + i, ["color"] = 0x80d8bb, ["iconUnicode"] = "★" },
                ["createdAt"] = "2026-09-13T10:00:00Z",
                ["runs"] = last
                    ? new JsonArray(new JsonObject { ["kind"] = "Text", ["text"] = "원본 프레임 시간 · 인라인 " }, new JsonObject { ["kind"] = "CustomEmoji", ["text"] = "검증이모지", ["mediaId"] = "fixture-emoji" }, new JsonObject { ["kind"] = "Text", ["text"] = " · 끝까지 표시" })
                    : new JsonArray(new JsonObject { ["kind"] = "Text", ["text"] = "합성 메시지입니다. 위로 스크롤하면 미디어 작업이 멈춥니다." }),
                ["media"] = last ? new JsonArray(new JsonObject { ["id"] = "fixture-animation", ["name"] = "가변 시간 GIF", ["kind"] = "Image" }) : new JsonArray(),
                ["reactions"] = last ? new JsonArray(new JsonObject { ["emoji"] = new JsonObject { ["kind"] = "CustomEmoji", ["text"] = "같은이모지", ["mediaId"] = "fixture-emoji-alias" }, ["count"] = 3 }) : new JsonArray(),
                ["forwarded"] = new JsonArray(),
                ["details"] = new JsonArray(),
                ["reply"] = null
            });
        }
        // The first attachment stays an animation; a separate forwarded still
        // checks alpha/color without changing Full's first + N attachment rule.
        rows[19]!["forwarded"] = new JsonArray(new JsonObject
        {
            ["runs"] = new JsonArray(new JsonObject { ["kind"] = "Text", ["text"] = "전달된 정적 이미지" }),
            ["media"] = new JsonArray(new JsonObject { ["id"] = "fixture-image", ["name"] = "반투명 sRGB", ["kind"] = "Image" })
        });
        var snapshot = new JsonObject { ["protocolVersion"] = 1, ["generation"] = "synthetic-media-v1", ["chat"] = rows };
        File.WriteAllText(Path.Combine(directory, "chat.json"), snapshot.ToJsonString());
    }
}
