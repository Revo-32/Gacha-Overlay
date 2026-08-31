namespace GachaOverlay.Core.Hud.Game;

public sealed class TargetGameMatcher
{
    public static IReadOnlyList<string> DefaultProcessNames { get; } =
        new[] { "GTA5", "GTA5_Enhanced" };

    private readonly HashSet<string> _processNames;

    public TargetGameMatcher(IEnumerable<string>? processNames = null)
    {
        _processNames = Normalize(processNames ?? DefaultProcessNames);
        if (_processNames.Count == 0)
        {
            _processNames = Normalize(DefaultProcessNames);
        }
    }

    public IReadOnlyCollection<string> ProcessNames => _processNames;

    public bool IsTarget(string? processName)
    {
        var normalized = NormalizeOne(processName);
        return normalized is not null && _processNames.Contains(normalized);
    }

    private static HashSet<string> Normalize(IEnumerable<string> names) =>
        names.Select(NormalizeOne)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeOne(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var fileName = Path.GetFileName(processName.Trim());
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(withoutExtension) ? null : withoutExtension;
    }
}
