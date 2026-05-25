using Dapper;

namespace TmTimeTracker.Data;

public sealed record MinuteSample(
    DateTime SampledAt,
    string? TicketKey,
    bool IsIdle,
    string? GitBranch,
    bool ClaudeRunning);

public sealed class MinuteSampleRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public MinuteSampleRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Insert(MinuteSample s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT OR REPLACE INTO minute_sample
                (sampled_at, ticket_key, is_idle, git_branch, claude_running)
              VALUES (@t, @k, @i, @b, @c)",
            new
            {
                t = s.SampledAt.ToString("O"),
                k = s.TicketKey,
                i = s.IsIdle ? 1 : 0,
                b = s.GitBranch,
                c = s.ClaudeRunning ? 1 : 0
            });
    }

    public int PruneOlderThan(DateTime cutoffUtc)
    {
        using var conn = _factory.Open();
        return conn.Execute("DELETE FROM minute_sample WHERE sampled_at < @c",
            new { c = cutoffUtc.ToString("O") });
    }

    public int Count()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM minute_sample");
    }
}
