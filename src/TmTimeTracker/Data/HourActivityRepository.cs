using System.Globalization;
using Dapper;

namespace TmTimeTracker.Data;

public sealed record HourActivityRow(DateTime HourStart, string RepoPath, string TicketKey, int Minutes);

/// <summary>
/// The hour-by-hour ledger the weekly report reads, written one minute at a time by
/// TimeAggregator. See the hour_activity comment in Schema.sql for why it has to exist at all.
///
/// Every timestamp crossing this class is LOCAL, because the question the report answers is a
/// local-calendar one.
/// </summary>
public sealed class HourActivityRepository
{
    // ':' and '-' are culture-sensitive placeholders in a custom format string, so the format
    // has to be applied with the invariant culture or a machine with different separators would
    // write hours that no longer sort or match.
    private const string HourFormat = "yyyy-MM-ddTHH:00:00";

    private readonly ISqliteConnectionFactory _factory;
    public HourActivityRepository(ISqliteConnectionFactory factory) => _factory = factory;

    private static string Key(DateTime local) =>
        new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0)
            .ToString(HourFormat, CultureInfo.InvariantCulture);

    /// <summary>Adds one minute to an (hour, repo, ticket) bucket, creating it if it is new.</summary>
    public void CreditMinute(DateTime hourStartLocal, string repoPath, string ticketKey)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO hour_activity (hour_start, repo_path, ticket_key, minutes)
              VALUES (@h, @r, @k, 1)
              ON CONFLICT(hour_start, repo_path, ticket_key)
              DO UPDATE SET minutes = minutes + 1",
            new { h = Key(hourStartLocal), r = repoPath, k = ticketKey ?? "" });
    }

    /// <summary>Lower bound inclusive, upper bound exclusive, ordered oldest first.</summary>
    public IReadOnlyList<HourActivityRow> GetBetween(DateTime fromLocal, DateTime toLocal)
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
            @"SELECT hour_start, repo_path, ticket_key, minutes FROM hour_activity
              WHERE hour_start >= @from AND hour_start < @to
              ORDER BY hour_start, repo_path, ticket_key",
            new { from = Key(fromLocal), to = Key(toLocal) })
            .Select(r => r.ToRow()).ToList();
    }

    public int PruneOlderThan(DateTime cutoffLocal)
    {
        using var conn = _factory.Open();
        return conn.Execute("DELETE FROM hour_activity WHERE hour_start < @c",
            new { c = Key(cutoffLocal) });
    }

    private sealed class Row
    {
        public string hour_start { get; set; } = "";
        public string repo_path { get; set; } = "";
        public string ticket_key { get; set; } = "";
        public int minutes { get; set; }

        public HourActivityRow ToRow() => new(
            DateTime.ParseExact(hour_start, HourFormat, CultureInfo.InvariantCulture),
            repo_path, ticket_key, minutes);
    }
}
