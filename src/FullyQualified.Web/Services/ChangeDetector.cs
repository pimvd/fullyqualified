namespace FullyQualified.Web;

public static class ChangeDetector
{
    /// <summary>Compares baseline values of two runs. Keys starting with '_' are informational and skipped.</summary>
    public static List<(string Key, string Before, string After)> Diff(
        IReadOnlyDictionary<string, string>? previous, IReadOnlyDictionary<string, string> current)
    {
        var changes = new List<(string, string, string)>();
        if (previous is null) return changes;
        foreach (var key in previous.Keys.Union(current.Keys).Where(k => !k.StartsWith('_')).OrderBy(k => k))
        {
            previous.TryGetValue(key, out var before);
            current.TryGetValue(key, out var after);
            if ((before ?? "") != (after ?? "")) changes.Add((key, before ?? "", after ?? ""));
        }
        return changes;
    }
}
