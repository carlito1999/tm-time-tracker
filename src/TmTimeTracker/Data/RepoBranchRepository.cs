using Dapper;

namespace TmTimeTracker.Data;

public sealed record RepoBranchOverride(string RepoPath, string BranchPattern);

/// <summary>
/// Which branch a repo's estimates run against, when origin/HEAD is not the right answer.
///
/// Absent is the normal case and means "use the remote's default branch". The override exists
/// because a repo's default branch is not always the one carrying the code - one tracked repo
/// points origin/HEAD at a stub commit while real work lands on rolling dated branches. A
/// pattern with '*' resolves to the newest matching branch, so that convention keeps working
/// without anyone updating this when the next branch is cut.
/// </summary>
public sealed class RepoBranchRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public RepoBranchRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(string repoPath, string branchPattern)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO repo_branch (repo_path, branch_pattern) VALUES (@p, @b)
              ON CONFLICT(repo_path) DO UPDATE SET branch_pattern = excluded.branch_pattern",
            new { p = repoPath, b = branchPattern });
    }

    public string? Find(string repoPath)
    {
        using var conn = _factory.Open();
        return conn.QueryFirstOrDefault<string>(
            "SELECT branch_pattern FROM repo_branch WHERE repo_path = @p", new { p = repoPath });
    }

    public void Clear(string repoPath)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM repo_branch WHERE repo_path = @p", new { p = repoPath });
    }

    public IReadOnlyList<RepoBranchOverride> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>("SELECT * FROM repo_branch ORDER BY repo_path")
            .Select(r => new RepoBranchOverride(r.repo_path, r.branch_pattern)).ToList();
    }

    private sealed class Row
    {
        public string repo_path { get; set; } = "";
        public string branch_pattern { get; set; } = "";
    }
}
