using Dapper;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Data;

public sealed record StoredRememberEntry(
    long Id,
    string TicketKey,
    string TimestampLocal,
    string EntryDate,
    string Body,
    string SourceFile,
    long? ConsumedInWorklog);

public sealed class RememberEntryRepository
{
    private readonly ISqliteConnectionFactory _factory;

    public RememberEntryRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void UpsertMany(IEnumerable<RememberEntry> entries, string entryDate)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        foreach (var e in entries)
        {
            if (e.TicketKey is null) continue;
            conn.Execute(
                @"INSERT OR IGNORE INTO remember_entry
                    (ticket_key, timestamp_local, entry_date, body, source_file)
                  VALUES (@k, @t, @d, @b, @f)",
                new
                {
                    k = e.TicketKey,
                    t = e.TimeOfDay,
                    d = entryDate,
                    b = e.Body,
                    f = e.SourceFile
                }, tx);
        }
        tx.Commit();
    }

    public IReadOnlyList<StoredRememberEntry> GetForTicketAndDate(string ticketKey, string entryDate)
    {
        using var conn = _factory.Open();
        return conn.Query<RememberRow>(
            @"SELECT * FROM remember_entry
              WHERE ticket_key=@k AND entry_date=@d
              ORDER BY timestamp_local",
            new { k = ticketKey, d = entryDate })
            .Select(r => r.ToRecord()).ToList();
    }

    public IReadOnlyList<StoredRememberEntry> GetUnconsumedForTicket(string ticketKey)
    {
        using var conn = _factory.Open();
        return conn.Query<RememberRow>(
            @"SELECT * FROM remember_entry
              WHERE ticket_key=@k AND consumed_in_worklog IS NULL
              ORDER BY entry_date, timestamp_local",
            new { k = ticketKey })
            .Select(r => r.ToRecord()).ToList();
    }

    public void TagConsumed(IEnumerable<long> entryIds, long ticketTimeRowId)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE remember_entry SET consumed_in_worklog=@w WHERE id IN @ids",
            new { w = ticketTimeRowId, ids = entryIds.ToArray() });
    }

    private sealed class RememberRow
    {
        public long id { get; set; }
        public string ticket_key { get; set; } = "";
        public string timestamp_local { get; set; } = "";
        public string entry_date { get; set; } = "";
        public string body { get; set; } = "";
        public string source_file { get; set; } = "";
        public long? consumed_in_worklog { get; set; }

        public StoredRememberEntry ToRecord() => new(
            id, ticket_key, timestamp_local, entry_date, body, source_file, consumed_in_worklog);
    }
}
