using LSOverlay.Protocol;

namespace LSOverlay.Backend.CoreClient;
// Intersect an explicit product allowlist with the viewer's authorized catalog.
// Ambiguous duplicate names fail closed; no arbitrary Discord ID input.
public sealed class ChannelSelection
{
    public static bool NeedsBootstrap(ulong requested, ulong scheduled, ulong committed, string status) => requested != scheduled || requested != committed || status != "live";
    public static bool NeedsCooldown(bool sales, bool userRequest, bool failed) => sales || !userRequest || failed;
    public static readonly string[] Names = ["메인", "1호실", "2호실", "3호실", "4호실", "5호실", "6호실", "잡떡"];
    public static readonly ulong[] Ids = [1428747924229193828, 1417852255943524392, 1417852429239844956, 1417859900440313927, 1417859919142457354, 1417859936561659914, 1448948335707820093, 1532413333435842742];
    private readonly Dictionary<int, ChatChannelDescriptor> _channels = new();
    private readonly object _gate = new();
    public void Update(ChatChannelCatalogResponse catalog)
    {
        if (catalog.ProtocolVersion != 1) throw new InvalidDataException("Invalid catalog version.");
        var candidates = catalog.Channels.Select(c => (Channel: c, Slot: Array.IndexOf(Ids, c.ChannelId))).Where(c => c.Slot >= 0).GroupBy(c => c.Slot);
        lock (_gate) { _channels.Clear(); foreach (var group in candidates) if (group.Count() == 1) _channels[group.Key] = group.Single().Channel; }
    }
    public ulong Resolve(int slot) { lock (_gate) return _channels.TryGetValue(slot, out var channel) ? channel.ChannelId : throw new UnauthorizedAccessException("Channel not selectable."); }
    public CoreChatSelection Describe(ulong selected) { lock (_gate) { var slot = _channels.Where(p => p.Value.ChannelId == selected).Select(p => p.Key).DefaultIfEmpty(-1).First(); return new(slot, slot >= 0 ? Names[slot] : "채팅방 연결 중", _channels.Keys.Order().ToArray()); } }
}

// Share only overlapping upstream reads. No TTL permission cache; every later
// check starts a fresh lookup, and each caller still checks local revocation.
public sealed class InFlightLookup<T>(Func<CancellationToken, Task<T>> factory, CancellationToken session)
{
    private readonly object _gate = new();
    private Task<T>? _pending;
    public Task<T> Get(CancellationToken caller)
    {
        Task<T> task; lock (_gate) { if (_pending is null || _pending.IsCompleted) _pending = factory(session); task = _pending; }
        return task.WaitAsync(caller);
    }
}
