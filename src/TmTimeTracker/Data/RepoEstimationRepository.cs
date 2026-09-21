using Dapper;

namespace TmTimeTracker.Data;

/// <summary>
/// Whether the estimation worker should estimate a tracked repo's To-Do tickets.
///
/// Enabled is the default and is stored as the absence of a row, so a repo only appears here
/// once the user switches it off on the Repositories tab. That keeps every repo tracked before
/// the switch existed estimating, with no migration.
/// </summary>
public sealed class RepoEstimationRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public RepoEstimationRepository(ISqliteConnectionFactory factory) => _factory = factory;

    // repo_path is COLLATE NOCASE, so the lookup is case-insensitive as Windows paths require.
    public bool IsEnabled(string repoPath)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM repo_estimation_disabled WHERE repo_path = @p",
            new { p = repoPath }) == 0;
    }

    public void SetEnabled(string repoPath, bool enabled)
    {
        using var conn = _factory.Open();
        conn.Execute(
            enabled
                ? "DELETE FROM repo_estimation_disabled WHERE repo_path = @p"
                : "INSERT OR IGNORE INTO repo_estimation_disabled (repo_path) VALUES (@p)",
            new { p = repoPath });
    }

    /// <summary>Forgets the repo, so tracking it again starts from the enabled default.</summary>
    public void Remove(string repoPath) => SetEnabled(repoPath, true);
}
