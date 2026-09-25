using System.Text.RegularExpressions;

namespace MediaCli.Transcode.Support;

/// <summary>
/// Port of lib/core.js helpers used by the transcode domain (formatArgs, roundNum...).
/// </summary>
public static partial class Core
{
    /// <summary>
    /// Port of lib/core.js formatArgs: supports %name%, {name}, @name@, !name! placeholders.
    /// Missing keys keep the placeholder as-is (same as JS).
    /// </summary>
    public static string FormatArgs(string? str, IReadOnlyDictionary<string, object?> replacements)
    {
        if (str is null) return "";
        var pattern = PlaceholderRegex();
        return pattern.Replace(str, match =>
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[4].Value;
            return replacements.TryGetValue(key, out var value) && value is not null
                ? value.ToString() ?? ""
                : match.Value;
        });
    }

    [GeneratedRegex(@"%([\w-]+)%|\{([\w-]+)\}|@([\w-]+)@|!([\w-]+)!")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Port of lib/core.js roundNum (default 2 decimal places).</summary>
    public static double RoundNum(double num, int decimalPlaces = 2)
        => Math.Round(num, decimalPlaces);

    /// <summary>Port of lib/core.js uniqueByFields (keeps first occurrence).</summary>
    public static List<T> UniqueByFields<T>(IEnumerable<T> items, Func<T, object?> keySelector)
    {
        var seen = new HashSet<object?>();
        var result = new List<T>();
        foreach (var item in items)
        {
            var key = keySelector(item);
            if (seen.Add(key)) result.Add(item);
        }
        return result;
    }

    /// <summary>Filters an entry list by filename rules: include/exclude substring + regex, then start/count slice.</summary>
    public static List<T> ApplyFileNameRules<T>(
        IEnumerable<T> entries,
        Func<T, string> nameSelector,
        string? include = null,
        string? exclude = null,
        string? regex = null,
        int start = 0,
        int count = int.MaxValue)
    {
        var filtered = new List<T>();
        Regex? re = null;
        if (!string.IsNullOrEmpty(regex))
        {
            re = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        foreach (var e in entries)
        {
            var name = nameSelector(e);
            if (!string.IsNullOrEmpty(include) && !name.Contains(include, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(exclude) && name.Contains(exclude, StringComparison.OrdinalIgnoreCase)) continue;
            if (re is not null && !re.IsMatch(name)) continue;
            filtered.Add(e);
        }
        if (start <= 0 && count >= filtered.Count) return filtered;
        return filtered.Skip(start).Take(count).ToList();
    }
}
