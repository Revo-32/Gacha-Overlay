using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Attention;

public enum AttentionEventType { DirectSelfMention, ReplyToSelf, SalesNextTurn, SalesCurrentTurn, GtaClientDetected, GtaClientLost }
public enum AttentionPriority { Informational, Medium, High }
public sealed record AttentionEntry(string Id, AttentionEventType Type, DateTimeOffset Timestamp,
    string SourceId, string Context, string Preview, bool IsRead, AttentionPriority Priority);
public sealed record AttentionHistoryState(int Version, string OwnerId, IReadOnlyList<AttentionEntry> Items);

/// <summary>Metadata only; owned by the application, independent of visual tree lifetimes.</summary>
public sealed class NotificationHistory
{
    public const int Capacity = 30;
    private readonly List<AttentionEntry> _items = [];
    public IReadOnlyList<AttentionEntry> Items => _items;
    public string OwnerId { get; private set; } = "local";
    public event Action? Changed;

    public void SetOwner(string owner)
    {
        if (OwnerId == owner) return;
        OwnerId = owner;
        _items.RemoveAll(x => x.Type is not (AttentionEventType.GtaClientDetected or AttentionEventType.GtaClientLost));
        Changed?.Invoke();
    }

    public void Restore(AttentionHistoryState? state)
    {
        if (state is not { Version: 1 } || string.IsNullOrWhiteSpace(state.OwnerId) || state.Items is null) return;
        OwnerId = Clip(state.OwnerId, 32);
        _items.Clear();
        _items.AddRange(state.Items.Where(x => x is not null && Enum.IsDefined(x.Type) && Enum.IsDefined(x.Priority) &&
                !string.IsNullOrWhiteSpace(x.Id) && x.Timestamp != default)
            .DistinctBy(x => x.Id).OrderByDescending(x => x.Timestamp).Take(Capacity)
            .Select(Sanitize));
        Changed?.Invoke();
    }

    public AttentionHistoryState Snapshot() => new(1, OwnerId, _items.ToArray());

    public void Add(AttentionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(OwnerId)) return;
        entry = Sanitize(entry);
        var index = _items.FindIndex(x => x.Id == entry.Id);
        if (index >= 0)
        {
            entry = entry with { IsRead = _items[index].IsRead, Timestamp = _items[index].Timestamp };
            if (_items[index] == entry) return;
            _items[index] = entry;
        }
        else _items.Insert(0, entry);
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
        Changed?.Invoke();
    }

    public void RemoveChat(string messageId)
    {
        if (_items.RemoveAll(x => IsChat(x.Type) && x.SourceId == messageId) > 0) Changed?.Invoke();
    }

    public void ObserveChat(NormalizedDiscordMessage message, bool allowNew)
    {
        var attention = ChatAttentionPolicy.Classify(message, OwnerId);
        var old = _items.FirstOrDefault(x => IsChat(x.Type) && x.SourceId == message.MessageId);
        if (attention < ChatAttention.ReplyToSelf) { RemoveChat(message.MessageId); return; }
        if (!allowNew && old is null) return; // Hydration is not a new attention event.
        var type = attention == ChatAttention.DirectSelfMention ? AttentionEventType.DirectSelfMention : AttentionEventType.ReplyToSelf;
        // One canonical message identity even when priority changes after an edit.
        Add(new("chat:" + message.MessageId, type, message.CreatedAt ?? DateTimeOffset.UtcNow,
            message.MessageId, ChatPresentationSynchronizer.ResolveAuthorName(message),
            Preview(message),
            old?.IsRead ?? false, attention == ChatAttention.DirectSelfMention ? AttentionPriority.High : AttentionPriority.Medium));
    }

    public void ObserveMutation(DiscordMessageMutation mutation, NormalizedDiscordMessage? message)
    {
        if (mutation.Kind == DiscordMessageMutationKind.Delete) { RemoveChat(mutation.MessageId); return; }
        if (message is not null) { ObserveChat(message, true); return; }
        if (!_items.Any(x => IsChat(x.Type) && x.SourceId == mutation.MessageId) || mutation.Patch is not { } patch) return;
        if (!patch.Content.HasValue) return;
        // Do not retain stale text for a source that has scrolled out of Latest20.
        if (!patch.Mentions.HasValue || !patch.RemoteMetadata.HasValue) { RemoveChat(mutation.MessageId); return; }
        ObserveChat(new(patch.MessageId, Value(patch.ChannelId) ?? "", Value(patch.AuthorId) ?? "", Value(patch.AuthorUsername) ?? "",
            Value(patch.AuthorDisplayName), patch.Content.Value ?? "", Value(patch.CreatedAt), Value(patch.EditedAt),
            [], [], [], patch.Mentions.Value ?? [])
        { RemoteMetadata = patch.RemoteMetadata.Value }, false);
    }

    private static string Preview(NormalizedDiscordMessage message)
    {
        var text = message.Content;
        foreach (var mention in message.Mentions)
        {
            var name = "@" + (mention.DisplayName ?? "사용자");
            text = text.Replace("<@" + mention.UserId + ">", name, StringComparison.Ordinal)
                .Replace("<@!" + mention.UserId + ">", name, StringComparison.Ordinal);
        }
        return string.Concat(ChatPresentationSynchronizer.TokenizeDiscordMarkup(text).Select(x => x.Text));
    }

    private static T? Value<T>(OptionalValue<T> value) => value.HasValue ? value.Value : default;

    public void MarkRead(string? id = null)
    {
        var changed = false;
        for (var i = 0; i < _items.Count; i++)
            if (!_items[i].IsRead && (id is null || _items[i].Id == id))
            { _items[i] = _items[i] with { IsRead = true }; changed = true; }
        if (changed) Changed?.Invoke();
    }

    private static bool IsChat(AttentionEventType type) => type is AttentionEventType.DirectSelfMention or AttentionEventType.ReplyToSelf;
    private static AttentionEntry Sanitize(AttentionEntry x) => x with
    { Id = Clip(x.Id, 128), SourceId = Clip(x.SourceId, 96), Context = Clip(x.Context, 80), Preview = Clip(x.Preview, 180) };
    private static string Clip(string? value, int length) => string.Concat((value ?? "").Where(c => !char.IsControl(c) || c == '\n')).Truncate(length);
}

internal static class BoundedAttentionText
{
    public static string Truncate(this string value, int length) => value.Length <= length ? value : value[..length];
}
