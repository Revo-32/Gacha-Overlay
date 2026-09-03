namespace GachaOverlay.App.Services;

/// <summary>Product presentation allowlist; Backend authorization remains mandatory.</summary>
internal static class MainChannelPolicy
{
    public static IReadOnlyList<(string Id, string Label)> Ordered { get; } = Array.AsReadOnly(new[]
    {
        ("1428747924229193828", "메인"), ("1417858541439680582", "습격임무구인"),
        ("1417852255943524392", "1호실"), ("1417852429239844956", "2호실"),
        ("1417859900440313927", "3호실"), ("1417859919142457354", "4호실"),
        ("1417859936561659914", "5호실"), ("1448948335707820093", "6호실"),
        ("1532413333435842742", "잡떡"),
    });

    public static RemoteChannelOption[] Apply(IEnumerable<RemoteChannelOption> accessible)
    {
        var byId = accessible.GroupBy(channel => channel.ChannelId).ToDictionary(group => group.Key, group => group.First());
        return Ordered.Where(item => byId.ContainsKey(item.Id)).Select(item =>
        {
            var channel = byId[item.Id];
            return string.IsNullOrWhiteSpace(channel.Name) ? channel with { Name = item.Label } : channel;
        }).ToArray();
    }

    public static string? Step(IReadOnlyList<RemoteChannelOption> channels, string? current, int direction)
    {
        if (channels.Count == 0) return null;
        var index = channels.ToList().FindIndex(channel => channel.ChannelId == current);
        if (index < 0) return channels[0].ChannelId;
        return channels[(index + (direction < 0 ? -1 : 1) + channels.Count) % channels.Count].ChannelId;
    }
}
