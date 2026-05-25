using System.Text;
using TmTimeTracker.Data;

namespace TmTimeTracker.Logic;

public static class WorklogDescriptionBuilder
{
    private const int MaxBytes = 32_000;
    private const string TruncMarker = "…(truncated)";

    public static string Build(IReadOnlyList<StoredRememberEntry> entries)
    {
        if (entries.Count == 0) return "(no .remember/ entries captured)";

        var dateGroups = entries
            .GroupBy(e => e.EntryDate)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var today = dateGroups[^1].Key;

        var sb = new StringBuilder();
        foreach (var group in dateGroups)
        {
            var sorted = group.OrderBy(e => e.TimestampLocal, StringComparer.Ordinal);
            var prefixDate = group.Key != today;
            foreach (var e in sorted)
            {
                if (sb.Length > 0) sb.Append('\n');
                var label = prefixDate ? $"{group.Key} {e.TimestampLocal}" : e.TimestampLocal;
                sb.Append("- [").Append(label).Append("] ");
                var bodyLines = e.Body.Split('\n');
                for (var i = 0; i < bodyLines.Length; i++)
                {
                    if (i > 0) sb.Append("\n  ");
                    sb.Append(bodyLines[i]);
                }
            }
        }

        return Truncate(sb.ToString());
    }

    private static string Truncate(string s)
    {
        var bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes <= MaxBytes) return s;
        var keepBytes = MaxBytes - Encoding.UTF8.GetByteCount(TruncMarker);
        var approxChars = (int)((double)keepBytes / bytes * s.Length);
        var trimmed = s.Substring(0, Math.Min(approxChars, s.Length));
        while (Encoding.UTF8.GetByteCount(trimmed) + Encoding.UTF8.GetByteCount(TruncMarker) > MaxBytes)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed + TruncMarker;
    }
}
