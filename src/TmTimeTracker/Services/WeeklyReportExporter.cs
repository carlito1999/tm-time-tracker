using System.Globalization;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

/// <summary>
/// Builds the hour-by-hour report, either as rows for the export window's preview or as an
/// .xlsx on disk. Both go through the same BuildGrid, so what the preview shows is what the
/// file contains.
///
/// Everything here works in local time, because hour_activity stores local hours and the
/// question the report answers - "what did Tuesday morning look like" - is a local one.
/// </summary>
public sealed class WeeklyReportExporter
{
    public const int DefaultDayStartHour = 8;
    public const int DefaultDayEndHour = 17;

    private const string SheetName = "Week Log";
    private static readonly string[] Headers = { "Date", "Time", "Repo", "Ticket" };

    private readonly HourActivityRepository _hours;
    private readonly TicketSummaryRepository _summaries;
    private readonly IClock _clock;

    public WeeklyReportExporter(HourActivityRepository hours, TicketSummaryRepository summaries,
        IClock clock)
    {
        _hours = hours; _summaries = summaries; _clock = clock;
    }

    /// <summary>
    /// Where a report lands by default. Documents rather than the app's own data directory: this
    /// is a deliverable to send someone, not state the app keeps.
    /// </summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TmTimeTracker");

    /// <summary>Monday of the current week through today - what the dialog opens on.</summary>
    public (DateTime From, DateTime To) CurrentWeek()
    {
        var today = _clock.LocalNow.DateTime.Date;
        // DayOfWeek counts from Sunday; shifting by six makes Monday the start of the week.
        var sinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return (today.AddDays(-sinceMonday), today);
    }

    public static string SuggestFileName(DateTime weekStart) =>
        $"TmTimeTracker-week-{weekStart:yyyy-MM-dd}.xlsx";

    public IReadOnlyList<GridRow> BuildGrid(DateTime from, DateTime to, int dayStartHour, int dayEndHour)
    {
        // The upper bound is exclusive in the ledger, so the last chosen day is included whole.
        var rows = _hours.GetBetween(from.Date, to.Date.AddDays(1));

        var keys = rows
            .Select(r => r.TicketKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return WeekGrid.Build(rows, from, to, dayStartHour, dayEndHour, _summaries.GetMany(keys));
    }

    public byte[] BuildWorkbook(IReadOnlyList<GridRow> grid) =>
        XlsxWriter.Write(SheetName, Headers,
            grid.Select(r => (IReadOnlyList<string>)new[] { r.Date, r.TimeSlot, r.Repo, r.Tickets })
                .ToList());

    /// <summary>Writes the report and returns the full path it landed on.</summary>
    public string Export(DateTime from, DateTime to, int dayStartHour, int dayEndHour,
        string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CleanFileName(fileName, from));
        File.WriteAllBytes(path, BuildWorkbook(BuildGrid(from, to, dayStartHour, dayEndHour)));
        return path;
    }

    /// <summary>
    /// The filename box is free text, so it can arrive blank, without an extension, or carrying
    /// characters Windows will not accept.
    /// </summary>
    private static string CleanFileName(string fileName, DateTime from)
    {
        var stripped = new string((fileName ?? "")
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();

        if (stripped.Length == 0) return SuggestFileName(from);

        return Path.GetExtension(stripped).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? stripped
            : stripped + ".xlsx";
    }
}
