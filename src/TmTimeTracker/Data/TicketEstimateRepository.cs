using System.Globalization;
using Dapper;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Data;

public sealed record TicketEstimateRow(
    string TicketKey, string RepoPath, string Status, int Attempts,
    int? ImplementationMinutes, int? TestingMinutes, int? ReviewMinutes,
    string? Confidence, string? Rationale, string? RawOutput,
    string? Error, string? FailedGate, DateTime? EstimatedAtUtc, DateTime? WarnedAtUtc);

/// <summary>
/// One row per ticket ever considered for estimation.
///
/// State is durable rather than in-memory for three reasons: the 5-minute sweep would
/// otherwise re-estimate every To-Do ticket after a restart, the two-attempt cap could never
/// be reached, and the user would be re-notified about the same failure on every restart.
/// </summary>
public sealed class TicketEstimateRepository
{
    private readonly ISqliteConnectionFactory _factory;

    public TicketEstimateRepository(ISqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Inserts a pending row, leaving an existing one untouched. The sweep re-queues everything
    /// it sees, so resetting here would clear the attempt count and let a permanently broken
    /// ticket spawn a Claude session every five minutes forever.
    /// </summary>
    public void QueueIfMissing(string ticketKey, string repoPath)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT OR IGNORE INTO ticket_estimate (ticket_key, repo_path, status, attempts)
              VALUES (@k, @p, @s, 0)",
            new { k = ticketKey, p = repoPath, s = EstimateStatus.Pending });
    }

    public int RecordAttempt(string ticketKey)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ticket_estimate SET attempts = attempts + 1 WHERE ticket_key = @k",
            new { k = ticketKey });
        return conn.ExecuteScalar<int>(
            "SELECT attempts FROM ticket_estimate WHERE ticket_key = @k", new { k = ticketKey });
    }

    public void MarkDone(string ticketKey, TicketEstimate estimate, string? rawOutput, DateTime nowUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ticket_estimate
                 SET status = @s, impl_minutes = @i, test_minutes = @t, review_minutes = @r,
                     confidence = @c, rationale = @ra, raw_output = @raw,
                     error = NULL, failed_gate = NULL, estimated_at = @at
               WHERE ticket_key = @k",
            new
            {
                k = ticketKey,
                s = EstimateStatus.Done,
                i = estimate.ImplementationMinutes,
                t = estimate.TestingMinutes,
                r = estimate.ReviewMinutes,
                c = estimate.Confidence,
                ra = estimate.Rationale,
                raw = rawOutput,
                at = Iso(nowUtc)
            });
    }

    public void MarkFailed(string ticketKey, EstimateGate? gate, string error)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ticket_estimate SET status = @s, error = @e, failed_gate = @g
               WHERE ticket_key = @k",
            new { k = ticketKey, s = EstimateStatus.Failed, e = error, g = gate?.ToString() });
    }

    public void MarkSkippedExisting(string ticketKey)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ticket_estimate SET status = @s WHERE ticket_key = @k",
            new { k = ticketKey, s = EstimateStatus.SkippedExisting });
    }

    public void MarkWarned(string ticketKey, DateTime nowUtc)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ticket_estimate SET warned_at = @w WHERE ticket_key = @k",
            new { k = ticketKey, w = Iso(nowUtc) });
    }

    public TicketEstimateRow? Find(string ticketKey)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM ticket_estimate WHERE ticket_key = @k", new { k = ticketKey });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<TicketEstimateRow> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>("SELECT * FROM ticket_estimate ORDER BY ticket_key")
            .Select(Map).ToList();
    }

    private static TicketEstimateRow Map(Row r) => new(
        r.ticket_key, r.repo_path, r.status, r.attempts,
        r.impl_minutes, r.test_minutes, r.review_minutes,
        r.confidence, r.rationale, r.raw_output,
        r.error, r.failed_gate, ParseUtc(r.estimated_at), ParseUtc(r.warned_at));

    private static string Iso(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);

    // Written with "o", which encodes the kind, so RoundtripKind restores UTC on its own.
    private static DateTime? ParseUtc(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                      .ToUniversalTime();

    private sealed class Row
    {
        public string ticket_key { get; set; } = "";
        public string repo_path { get; set; } = "";
        public string status { get; set; } = "";
        public int attempts { get; set; }
        public int? impl_minutes { get; set; }
        public int? test_minutes { get; set; }
        public int? review_minutes { get; set; }
        public string? confidence { get; set; }
        public string? rationale { get; set; }
        public string? raw_output { get; set; }
        public string? error { get; set; }
        public string? failed_gate { get; set; }
        public string? estimated_at { get; set; }
        public string? warned_at { get; set; }
    }
}
