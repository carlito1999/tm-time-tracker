using System.Globalization;
using TmTimeTracker.Data;

namespace TmTimeTracker.Logic;

/// <summary>One line of the report. Every cell is already rendered, so the writer only places text.</summary>
public sealed record GridRow(string Date, string TimeSlot, string Repo, string Tickets);

/// <summary>
/// Turns the hour ledger into the hour-by-hour sheet.
///
/// Every hour of the chosen day window gets a row whether or not it earned any minutes: an empty
/// hour keeps its place so the shape of the day survives and a gap stays visible.
/// </summary>
public static class WeekGrid
{
    /// <summary>
    /// Below this an entry is treated as noise rather than work. TimeAggregator credits every
    /// active repo concurrently, so without a floor a one-minute Claude write would put a whole
    /// extra repo in the cell.
    ///
    /// It defaults to zero - print everything tracked - because a silently filtered sheet reads
    /// as "nothing was recorded" rather than "below your threshold", and pruning a full sheet is
    /// easier than noticing work missing from a sparse one. TimeAggregator already gates on idle
    /// and real activity before a minute is credited, so this is a second filter on top of one
    /// that has done most of the work. The export window exposes it for crowded hours.
    ///
    /// Applied to the repo's total for the hour but to each ticket separately, deliberately: an
    /// hour split four minutes each across two tickets of one repo is eight minutes in that repo,
    /// so the repo earns its cell even though neither ticket does.
    /// </summary>
    public const int DefaultMinimumMinutes = 0;

    private const string DateFormat = "dd-MM-yy";
    private const char EnDash = '–';

    public static IReadOnlyList<GridRow> Build(
        IReadOnlyList<HourActivityRow> rows,
        DateTime from,
        DateTime to,
        int dayStartHour,
        int dayEndHour,
        IReadOnlyDictionary<string, string> summaries,
        int minimumMinutes = DefaultMinimumMinutes)
    {
        // The pickers hand back a whole DateTime; only the date half selects days.
        var first = from.Date;
        var last = to.Date;
        if (first > last || dayStartHour >= dayEndHour) return Array.Empty<GridRow>();

        var byHour = rows
            .GroupBy(r => r.HourStart)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<HourActivityRow>)g.ToList());

        var grid = new List<GridRow>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            // Between days only, never trailing - the sheet should not end on an empty line.
            if (grid.Count > 0) grid.Add(new GridRow("", "", "", ""));

            var date = day.ToString(DateFormat, CultureInfo.InvariantCulture);
            for (var hour = dayStartHour; hour < dayEndHour; hour++)
            {
                byHour.TryGetValue(day.AddHours(hour), out var inHour);
                grid.Add(new GridRow(
                    date,
                    $"{hour:00}:00{EnDash}{hour + 1:00}:00",
                    RenderRepos(inHour, minimumMinutes),
                    RenderTickets(inHour, summaries, minimumMinutes)));
            }
        }

        return grid;
    }

    private static string RenderRepos(IReadOnlyList<HourActivityRow>? inHour, int minimumMinutes)
    {
        if (inHour is null) return "";

        return string.Join(" / ", inHour
            .GroupBy(r => r.RepoPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = FolderName(g.Key), Minutes = g.Sum(r => r.Minutes) })
            .Where(x => x.Minutes >= minimumMinutes)
            .OrderByDescending(x => x.Minutes)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name));
    }

    private static string RenderTickets(IReadOnlyList<HourActivityRow>? inHour,
        IReadOnlyDictionary<string, string> summaries, int minimumMinutes)
    {
        if (inHour is null) return "";

        return string.Join("\n", inHour
            // An empty key is a minute on a branch carrying no ticket. It counts towards the repo
            // above, but there is nothing to name here.
            .Where(r => r.TicketKey.Length > 0)
            .GroupBy(r => r.TicketKey, StringComparer.Ordinal)
            .Select(g => new { Key = g.Key, Minutes = g.Sum(r => r.Minutes) })
            .Where(x => x.Minutes >= minimumMinutes)
            .OrderByDescending(x => x.Minutes)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => summaries.TryGetValue(x.Key, out var s) && !string.IsNullOrWhiteSpace(s)
                ? $"{x.Key}: {s}"
                : x.Key));
    }

    /// <summary>
    /// tracked_repo stores a full path and has no display-name column, so the folder is the name -
    /// the same derivation ActiveRepoResolver and the dashboard already use.
    /// </summary>
    private static string FolderName(string repoPath)
    {
        var trimmed = repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }
}
