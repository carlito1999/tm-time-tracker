using Dapper;

namespace TmTimeTracker.Data;

public sealed record AppConfig(
    int IdleThresholdSeconds,
    int JiraPollIntervalSeconds,
    string RepoPath,
    string RememberPath,
    string InProgressStatusName,
    string TransitionToStatusName);

public sealed class ConfigRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public ConfigRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void SetIfMissing(AppConfig defaults)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT OR IGNORE INTO config
                (id, idle_threshold_seconds, jira_poll_interval_seconds,
                 repo_path, remember_path, in_progress_status_name, transition_to_status_name)
              VALUES (1, @it, @pi, @rp, @rm, @ip, @tr)",
            new
            {
                it = defaults.IdleThresholdSeconds,
                pi = defaults.JiraPollIntervalSeconds,
                rp = defaults.RepoPath,
                rm = defaults.RememberPath,
                ip = defaults.InProgressStatusName,
                tr = defaults.TransitionToStatusName
            });
    }

    public AppConfig Get()
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingle<ConfigRow>("SELECT * FROM config WHERE id=1");
        return new AppConfig(
            row.idle_threshold_seconds, row.jira_poll_interval_seconds,
            row.repo_path, row.remember_path,
            row.in_progress_status_name, row.transition_to_status_name);
    }

    private sealed class ConfigRow
    {
        public int idle_threshold_seconds { get; set; }
        public int jira_poll_interval_seconds { get; set; }
        public string repo_path { get; set; } = "";
        public string remember_path { get; set; } = "";
        public string in_progress_status_name { get; set; } = "";
        public string transition_to_status_name { get; set; } = "";
    }
}
