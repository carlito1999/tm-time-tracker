using System.Globalization;

namespace TmTimeTracker.Logic;

public sealed record SlackVariable(string Name, string Description, string Sample);

public static class SlackVariables
{
    public const string DefaultTemplate =
        "Done ✅ {TICKET} — {SUMMARY}\n{FROM} -> {TO} · {HOURS}\n{URL}";

    /// <summary>
    /// The variables the Settings page advertises. <see cref="Build"/> must be able to emit
    /// exactly these names — a test asserts the two never drift apart.
    /// </summary>
    public static IReadOnlyList<SlackVariable> Catalog { get; } = new[]
    {
        new SlackVariable("TICKET",  "Issue key",           "SN-296"),
        new SlackVariable("PROJECT", "Project key",         "SN"),
        new SlackVariable("SUMMARY", "Issue summary",       "Fix Tolgee warning"),
        new SlackVariable("URL",     "Link to the issue",   "https://example.atlassian.net/browse/SN-296"),
        new SlackVariable("FROM",    "Previous status",     "In Progress"),
        new SlackVariable("TO",      "New status",          "Review"),
        new SlackVariable("MINUTES", "Tracked minutes",     "137"),
        new SlackVariable("HOURS",   "Tracked time",        "2h 17m"),
        new SlackVariable("DATE",    "Local date and time", "2026-09-02 14:31"),
    };

    /// <summary>
    /// A variable whose value is unavailable is OMITTED rather than mapped to "". Omission is what
    /// makes an unresolved value render as a literal placeholder instead of a silent blank.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        string ticketKey, string? summary, string fromStatus, string toStatus,
        int minutesActive, DateTime atUtc, string? siteUrl)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TICKET"]  = ticketKey,
            ["FROM"]    = fromStatus,
            ["TO"]      = toStatus,
            ["MINUTES"] = minutesActive.ToString(CultureInfo.InvariantCulture),
            ["HOURS"]   = FormatHours(minutesActive),
            ["DATE"]    = atUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };

        var project = JiraProjectKey.From(ticketKey);
        if (project is not null) vars["PROJECT"] = project;

        if (!string.IsNullOrWhiteSpace(summary)) vars["SUMMARY"] = summary;

        if (!string.IsNullOrWhiteSpace(siteUrl))
            vars["URL"] = $"{siteUrl.TrimEnd('/')}/browse/{ticketKey}";

        return vars;
    }

    /// <summary>Sample values, used for the Settings live preview and the test message.</summary>
    public static IReadOnlyDictionary<string, string> Sample() =>
        Catalog.ToDictionary(c => c.Name, c => c.Sample, StringComparer.Ordinal);

    public static string FormatHours(int minutes)
    {
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }
}
