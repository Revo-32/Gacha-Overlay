namespace GachaOverlay.Core.Chat;

public static class ChatAuthorGrouping
{
    public static IReadOnlyList<bool> ResolveHeaders(IEnumerable<string?> authorIds)
    {
        ArgumentNullException.ThrowIfNull(authorIds);
        var result = new List<bool>();
        string? previous = null;
        foreach (var authorId in authorIds)
        {
            var current = authorId ?? string.Empty;
            result.Add(previous is null ||
                !string.Equals(previous, current, StringComparison.Ordinal));
            previous = current;
        }

        return result;
    }
}
