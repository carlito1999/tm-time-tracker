using System.Globalization;
using Dapper;

namespace TmTimeTracker.Data;

/// <summary>
/// Ticket key to its Jira summary, so a report can name a ticket offline. Written by the poll
/// that already had the summary in hand; see the ticket_summary comment in Schema.sql.
/// </summary>
public sealed class TicketSummaryRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public TicketSummaryRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Upsert(string ticketKey, string summary)
    {
        if (string.IsNullOrWhiteSpace(ticketKey) || summary is null) return;
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO ticket_summary (ticket_key, summary, updated_at)
              VALUES (@k, @s, @u)
              ON CONFLICT(ticket_key) DO UPDATE SET summary = @s, updated_at = @u",
            new { k = ticketKey, s = summary, u = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
    }

    /// <summary>
    /// Keys with no cached summary are simply absent, so a caller renders the bare key for them.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetMany(IReadOnlyCollection<string> ticketKeys)
    {
        if (ticketKeys.Count == 0) return new Dictionary<string, string>();

        using var conn = _factory.Open();
        return conn.Query<(string Key, string Summary)>(
            "SELECT ticket_key, summary FROM ticket_summary WHERE ticket_key IN @keys",
            new { keys = ticketKeys })
            .ToDictionary(r => r.Key, r => r.Summary, StringComparer.Ordinal);
    }
}
