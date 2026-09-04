using System.Globalization;
using Dapper;

namespace TmTimeTracker.Data;

public sealed record PrAnnouncement(
    string TicketKey,
    string? IssueId,
    string? Summary,
    string FromStatus,
    string ToStatus,
    int Minutes,
    DateTime OccurredAtUtc,
    DateTime QueuedAtUtc,
    int Attempts,
    DateTime? AnnouncedAtUtc,
    string? PrUrl,
    DateTime? WarnedAtUtc,
    DateTime? HandedOffAtUtc = null)
{
    public bool IsAnnounced => AnnouncedAtUtc is not null;
    public bool HasWarned => WarnedAtUtc is not null;

    /// <summary>
    /// The daemon gave up waiting for a fresh pull request and asked the user to announce it by
    /// hand. Distinct from <see cref="HasWarned"/>: that one only stops the warning repeating,
    /// while this stops the announcement happening at all - posting after the user has been sent
    /// to do it themselves would duplicate the message.
    /// </summary>
    public bool IsHandedOff => HandedOffAtUtc is not null;
}

/// <summary>
/// Durable record of which tickets still owe a Slack announcement.
///
/// This lives in the database rather than in memory precisely so a restart cannot lose a pending
/// announcement - Jira can take many minutes to surface a pull request, which is longer than the
/// app is guaranteed to stay running.
/// </summary>
public sealed class PrAnnouncementRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public PrAnnouncementRepository(ISqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Queues a ticket if it is not already tracked. Never overwrites an existing row.</summary>
    public void QueueIfMissing(PrAnnouncement announcement)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT OR IGNORE INTO pr_announcement
                (ticket_key, issue_id, summary, from_status, to_status, minutes,
                 occurred_at, queued_at, attempts)
              VALUES (@key, @issue, @summary, @from, @to, @minutes, @occurred, @queued, 0)",
            new
            {
                key = announcement.TicketKey,
                issue = announcement.IssueId,
                summary = announcement.Summary,
                from = announcement.FromStatus,
                to = announcement.ToStatus,
                minutes = announcement.Minutes,
                occurred = Iso(announcement.OccurredAtUtc),
                queued = Iso(announcement.QueuedAtUtc)
            });
    }

    public IReadOnlyList<PrAnnouncement> GetPending()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
            @"SELECT * FROM pr_announcement
              WHERE announced_at IS NULL AND handed_off_at IS NULL
              ORDER BY queued_at")
            .Select(Map).ToList();
    }

    public PrAnnouncement? Find(string ticketKey)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM pr_announcement WHERE ticket_key = @key", new { key = ticketKey });
        return row is null ? null : Map(row);
    }

    public void MarkAnnounced(string ticketKey, string? prUrl, DateTime atUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE pr_announcement SET announced_at = @at, pr_url = @url WHERE ticket_key = @key",
            new { key = ticketKey, at = Iso(atUtc), url = prUrl });
    }

    public void MarkHandedOff(string ticketKey, DateTime atUtc)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE pr_announcement SET handed_off_at = @at WHERE ticket_key = @key",
            new { key = ticketKey, at = Iso(atUtc) });
    }

    public void MarkWarned(string ticketKey, DateTime atUtc)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE pr_announcement SET warned_at = @at WHERE ticket_key = @key",
            new { key = ticketKey, at = Iso(atUtc) });
    }

    public void RecordAttempt(string ticketKey)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE pr_announcement SET attempts = attempts + 1 WHERE ticket_key = @key",
            new { key = ticketKey });
    }

    /// <summary>
    /// Forgets a ticket entirely, so that if it returns to review later it is announced again.
    /// </summary>
    public void Remove(string ticketKey)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM pr_announcement WHERE ticket_key = @key", new { key = ticketKey });
    }

    public IReadOnlyList<string> GetAllTicketKeys()
    {
        using var conn = _factory.Open();
        return conn.Query<string>("SELECT ticket_key FROM pr_announcement").ToList();
    }

    private static string Iso(DateTime value) =>
        value.ToString("o", CultureInfo.InvariantCulture);

    // Values are written with "o", which encodes the kind, so RoundtripKind restores UTC on its own.
    // It cannot be combined with AdjustToUniversal - that pairing throws at runtime.
    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUniversalTime();

    private static PrAnnouncement Map(Row r) => new(
        r.ticket_key, r.issue_id, r.summary, r.from_status, r.to_status, r.minutes,
        ParseUtc(r.occurred_at), ParseUtc(r.queued_at), r.attempts,
        r.announced_at is null ? null : ParseUtc(r.announced_at),
        r.pr_url,
        r.warned_at is null ? null : ParseUtc(r.warned_at),
        r.handed_off_at is null ? null : ParseUtc(r.handed_off_at));

    private sealed class Row
    {
        public string ticket_key { get; set; } = "";
        public string? issue_id { get; set; }
        public string? summary { get; set; }
        public string from_status { get; set; } = "";
        public string to_status { get; set; } = "";
        public int minutes { get; set; }
        public string occurred_at { get; set; } = "";
        public string queued_at { get; set; } = "";
        public int attempts { get; set; }
        public string? announced_at { get; set; }
        public string? pr_url { get; set; }
        public string? warned_at { get; set; }
        public string? handed_off_at { get; set; }
    }
}
