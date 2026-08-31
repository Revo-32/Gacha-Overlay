namespace GachaOverlay.App.Services.Sales;

internal enum SalesCompletionReactionMarker
{
    None,
    Sold,
    Closed,
}

internal enum SalesCompletionReactionMatchSource
{
    None,
    EmojiId,
    NameFallback,
}

internal sealed record DiscordReactionIdentity(
    string? EmojiId,
    string? EmojiName);

internal readonly record struct SalesCompletionReactionMatch(
    SalesCompletionReactionMarker Marker,
    SalesCompletionReactionMatchSource Source)
{
    public bool IsCompletion => Marker != SalesCompletionReactionMarker.None;

    public static SalesCompletionReactionMatch None { get; } = new(
        SalesCompletionReactionMarker.None,
        SalesCompletionReactionMatchSource.None);
}

internal static class DiscordSalesCompletionReactionMatcher
{
    internal const string SoldEmojiId = "1451583544295034940";
    internal const string SoldEmojiName = "SOLD";
    internal const string ClosedEmojiId = "1418284521337651321";
    internal const string ClosedEmojiName = "closed";

    public static SalesCompletionReactionMatch Match(DiscordReactionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.EmojiId is not null)
        {
            return identity.EmojiId switch
            {
                SoldEmojiId => Match(
                    SalesCompletionReactionMarker.Sold,
                    SalesCompletionReactionMatchSource.EmojiId),
                ClosedEmojiId => Match(
                    SalesCompletionReactionMarker.Closed,
                    SalesCompletionReactionMatchSource.EmojiId),
                _ => SalesCompletionReactionMatch.None,
            };
        }

        return identity.EmojiName switch
        {
            SoldEmojiName => Match(
                SalesCompletionReactionMarker.Sold,
                SalesCompletionReactionMatchSource.NameFallback),
            ClosedEmojiName => Match(
                SalesCompletionReactionMarker.Closed,
                SalesCompletionReactionMatchSource.NameFallback),
            _ => SalesCompletionReactionMatch.None,
        };
    }

    public static SalesCompletionReactionMatch MatchAccessibleNameFallback(
        string? accessibleName)
    {
        if (ContainsExactIdentifier(accessibleName, SoldEmojiName))
        {
            return Match(new DiscordReactionIdentity(null, SoldEmojiName));
        }

        if (ContainsExactIdentifier(accessibleName, ClosedEmojiName))
        {
            return Match(new DiscordReactionIdentity(null, ClosedEmojiName));
        }

        return SalesCompletionReactionMatch.None;
    }

    private static bool ContainsExactIdentifier(string? value, string identifier)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var start = 0;
        while (start <= value.Length - identifier.Length)
        {
            var index = value.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var beforeIsIdentifier = index > 0 && IsAsciiIdentifier(value[index - 1]);
            var afterIndex = index + identifier.Length;
            var afterIsIdentifier = afterIndex < value.Length &&
                IsAsciiIdentifier(value[afterIndex]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    private static SalesCompletionReactionMatch Match(
        SalesCompletionReactionMarker marker,
        SalesCompletionReactionMatchSource source) => new(marker, source);

    private static bool IsAsciiIdentifier(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '_';
}
