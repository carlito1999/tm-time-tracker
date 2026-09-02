using Dapper;

namespace TmTimeTracker.Data;

public sealed class TicketTimeRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly object _openOrCreateGate = new();

    public TicketTimeRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public TicketCycle OpenOrCreateCycle(string ticketKey, DateTime cycleStartedUtc)
    {
        lock (_openOrCreateGate)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();

            var existing = conn.QueryFirstOrDefault<TicketCycleRow>(
                "SELECT * FROM ticket_time WHERE ticket_key=@k AND submitted_at IS NULL LIMIT 1",
                new { k = ticketKey }, tx);

            if (existing is not null)
            {
                tx.Commit();
                return existing.ToCycle();
            }

            var iso = cycleStartedUtc.ToString("O");
            var id = conn.ExecuteScalar<long>(
                @"INSERT INTO ticket_time (ticket_key, cycle_started, minutes_active)
                  VALUES (@k, @c, 0);
                  SELECT last_insert_rowid();",
                new { k = ticketKey, c = iso }, tx);
            tx.Commit();

            return new TicketCycle(id, ticketKey, cycleStartedUtc, 0, null, null, null, null, null);
        }
    }

    public void IncrementMinute(long cycleId)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ticket_time SET minutes_active = minutes_active + 1 WHERE id=@id",
            new { id = cycleId });
    }

    /// <summary>
    /// Credits a block of minutes at once - used to hand a ticket the time that was spent before
    /// its branch was checked out.
    /// </summary>
    public void AddMinutes(long cycleId, int minutes)
    {
        if (minutes <= 0) return;
        using var conn = _factory.Open();
        conn.Execute("UPDATE ticket_time SET minutes_active = minutes_active + @minutes WHERE id=@id",
            new { id = cycleId, minutes });
    }

    public TicketCycle? GetById(long id)
    {
        using var conn = _factory.Open();
        return conn.QueryFirstOrDefault<TicketCycleRow>(
            "SELECT * FROM ticket_time WHERE id=@id", new { id })?.ToCycle();
    }

    public IReadOnlyList<TicketCycle> GetAllOpen()
    {
        using var conn = _factory.Open();
        return conn.Query<TicketCycleRow>(
            "SELECT * FROM ticket_time WHERE submitted_at IS NULL ORDER BY id")
            .Select(r => r.ToCycle()).ToList();
    }

    /// <summary>
    /// Every ticket key ever tracked. Settings derives the project list from this so the user
    /// never types a project key by hand.
    /// </summary>
    public IReadOnlyList<string> GetDistinctTicketKeys()
    {
        using var conn = _factory.Open();
        return conn.Query<string>(
            "SELECT DISTINCT ticket_key FROM ticket_time ORDER BY ticket_key").ToList();
    }

    public void MarkSubmitted(long cycleId, string worklogId, int submittedMinutes, DateTime submittedAtUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ticket_time
              SET worklog_id=@w, submitted_minutes=@m, submitted_at=@s
              WHERE id=@id",
            new { id = cycleId, w = worklogId, m = submittedMinutes, s = submittedAtUtc.ToString("O") });
    }

    public void UpdateStatusSnapshot(long cycleId, string status, DateTime polledAtUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ticket_time
              SET last_seen_status=@s, last_polled=@p
              WHERE id=@id",
            new { id = cycleId, s = status, p = polledAtUtc.ToString("O") });
    }

    private sealed class TicketCycleRow
    {
        public long id { get; set; }
        public string ticket_key { get; set; } = "";
        public string cycle_started { get; set; } = "";
        public int minutes_active { get; set; }
        public string? last_seen_status { get; set; }
        public string? last_polled { get; set; }
        public string? submitted_at { get; set; }
        public string? worklog_id { get; set; }
        public int? submitted_minutes { get; set; }

        public TicketCycle ToCycle() => new(
            Id: id,
            TicketKey: ticket_key,
            CycleStarted: DateTime.Parse(cycle_started, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            MinutesActive: minutes_active,
            LastSeenStatus: last_seen_status,
            LastPolled: last_polled is null ? null
                : DateTime.Parse(last_polled, null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            SubmittedAt: submitted_at is null ? null
                : DateTime.Parse(submitted_at, null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            WorklogId: worklog_id,
            SubmittedMinutes: submitted_minutes);
    }
}
