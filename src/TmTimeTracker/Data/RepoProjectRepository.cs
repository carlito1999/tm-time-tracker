using Dapper;

namespace TmTimeTracker.Data;

public sealed record RepoProjectMapping(string RepoPath, string ProjectKey, bool AutoMatched);

/// <summary>
/// Which Jira project's board covers a tracked repo. Populated by the auto-matcher and
/// correctable from the Repositories tab; <see cref="RepoProjectMapping.AutoMatched"/> records
/// which, so a hand-set mapping is never silently re-guessed.
/// </summary>
public sealed class RepoProjectRepository
{
    private readonly ISqliteConnectionFactory _factory;

    public RepoProjectRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(string repoPath, string projectKey, bool autoMatched)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO repo_project (repo_path, project_key, auto_matched)
              VALUES (@p, @k, @a)
              ON CONFLICT(repo_path) DO UPDATE SET project_key  = excluded.project_key,
                                                   auto_matched = excluded.auto_matched",
            new { p = repoPath, k = projectKey, a = autoMatched ? 1 : 0 });
    }

    // repo_path is COLLATE NOCASE, so the lookup is case-insensitive as Windows paths require.
    public string? Find(string repoPath)
    {
        using var conn = _factory.Open();
        return conn.QueryFirstOrDefault<string>(
            "SELECT project_key FROM repo_project WHERE repo_path = @p", new { p = repoPath });
    }

    public IReadOnlyList<RepoProjectMapping> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>("SELECT * FROM repo_project ORDER BY repo_path")
            .Select(r => new RepoProjectMapping(r.repo_path, r.project_key, r.auto_matched != 0))
            .ToList();
    }

    private sealed class Row
    {
        public string repo_path { get; set; } = "";
        public string project_key { get; set; } = "";
        public int auto_matched { get; set; }
    }
}
