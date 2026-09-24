using System.Globalization;
using Dapper;

namespace TmTimeTracker.Data;

public sealed record JevEstimateRow(
    string TicketKey, string Model, string AnswerJson,
    double ExpectedMinutes, double MiddleMinutes, double Confidence,
    int PredictedMinutes, DateTime EstimatedAtUtc);

/// <summary>
/// Jev's answer for each ticket it estimated, beside the minutes the estimator made of it.
///
/// Kept so the estimator can be refitted from answers already paid for: once a ticket's worklogs
/// are in, its row and its logged time are a training example with no further call to Jev.
/// </summary>
public sealed class JevEstimateRepository
{
    private readonly ISqliteConnectionFactory _factory;

    public JevEstimateRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(string ticketKey, string model, string answerJson, double expectedMinutes,
        double middleMinutes, double confidence, int predictedMinutes, DateTime estimatedAtUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO ticket_estimate_jev (ticket_key, model, answer_json, expected_minutes,
                  middle_minutes, confidence, predicted_minutes, estimated_at)
              VALUES (@k, @m, @a, @e, @mid, @c, @p, @at)
              ON CONFLICT(ticket_key) DO UPDATE SET
                  model = excluded.model, answer_json = excluded.answer_json,
                  expected_minutes = excluded.expected_minutes,
                  middle_minutes = excluded.middle_minutes, confidence = excluded.confidence,
                  predicted_minutes = excluded.predicted_minutes,
                  estimated_at = excluded.estimated_at",
            new
            {
                k = ticketKey, m = model, a = answerJson, e = expectedMinutes, mid = middleMinutes,
                c = confidence, p = predictedMinutes, at = estimatedAtUtc.ToString("O")
            });
    }

    public JevEstimateRow? Find(string ticketKey)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM ticket_estimate_jev WHERE ticket_key = @k", new { k = ticketKey });
        return row is null
            ? null
            : new JevEstimateRow(row.ticket_key, row.model, row.answer_json, row.expected_minutes,
                row.middle_minutes, row.confidence, (int)row.predicted_minutes,
                DateTime.Parse(row.estimated_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private sealed class Row
    {
        public string ticket_key { get; set; } = "";
        public string model { get; set; } = "";
        public string answer_json { get; set; } = "";
        public double expected_minutes { get; set; }
        public double middle_minutes { get; set; }
        public double confidence { get; set; }
        public long predicted_minutes { get; set; }
        public string estimated_at { get; set; } = "";
    }
}
