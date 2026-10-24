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
            // Values that are always present (a, aaaa, dkim.<selector>) are baselined the first time they are seen,
            // so adding the check, or a selector, does not log a change for something that was merely not looked at before.
            if (!previous.ContainsKey(key) && IsBaselinedOnFirstSight(key)) continue;
            previous.TryGetValue(key, out var before);
            current.TryGetValue(key, out var after);
            if ((before ?? "") != (after ?? "")) changes.Add((key, before ?? "", after ?? ""));
        }
        return changes;
    }

    static bool IsBaselinedOnFirstSight(string key) => key is "a" or "aaaa" || key.StartsWith("dkim.", StringComparison.Ordinal);
}
