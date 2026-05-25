using Dapper;

namespace TmTimeTracker.Data;

public sealed record TrackedRepo(long Id, string Path, int SortOrder);

public sealed class TrackedRepoRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public TrackedRepoRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Add(string path)
    {
        using var conn = _factory.Open();
        var nextOrder = conn.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM tracked_repo");
        conn.Execute(
            "INSERT OR IGNORE INTO tracked_repo (path, sort_order) VALUES (@p, @o)",
            new { p = path, o = nextOrder });
    }

    public void Remove(string path)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM tracked_repo WHERE path = @p COLLATE NOCASE",
            new { p = path });
    }

    public IReadOnlyList<TrackedRepo> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<TrackedRepoRow>(
            "SELECT id, path, sort_order FROM tracked_repo ORDER BY sort_order, id")
            .Select(r => new TrackedRepo(r.id, r.path, r.sort_order))
            .ToList();
    }

    private sealed class TrackedRepoRow
    {
        public long id { get; set; }
        public string path { get; set; } = "";
        public int sort_order { get; set; }
    }
}
