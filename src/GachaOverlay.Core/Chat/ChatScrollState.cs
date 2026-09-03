namespace GachaOverlay.Core.Chat;

/// <summary>Bounded semantic state: never owns messages, views or subscriptions.</summary>
public sealed class ChatScrollState
{
    public bool IsFollowingLatest { get; private set; } = true;
    public int UnreadCount { get; private set; }
    public void ObserveUserOffset(double offset, double scrollableHeight)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(scrollableHeight)) return;
        if (scrollableHeight - offset <= 2) FollowLatest();
        else IsFollowingLatest = false;
    }
    public void ReceiveNewMessage()
    {
        if (!IsFollowingLatest) UnreadCount = Math.Min(20, UnreadCount + 1);
    }
    public void FollowLatest() { IsFollowingLatest = true; UnreadCount = 0; }
}
