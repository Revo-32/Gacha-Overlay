namespace LSOverlay.Backend.Discord;

// Cosmetic only. This component deliberately has no reference to public health,
// message/reaction events, credentials, timers or a second Discord connection.
internal sealed class BotCustomStatus(Func<string, Task> setStatus, Action warn)
{
    internal const string Text = "LS Overlay - 24/7 가동 중";

    internal async Task ApplyAfterReadyAsync()
    {
        try
        {
            // A stalled cosmetic send must not hold the Gateway callback drain.
            await setStatus(Text).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never include the SDK exception message, payload or credentials.
            warn();
        }
    }
}
