using Dapper;

namespace TmTimeTracker.Data;

public sealed record JiraSite(string CloudId, string SiteUrl);

/// <summary>
/// One-row cache of the Jira site URL (e.g. https://acme.atlassian.net), which oauth_state does
/// not store. Kept in its own table rather than as a column on oauth_state because Schema.sql is
/// CREATE TABLE IF NOT EXISTS only - a new column would never appear on an existing database.
/// </summary>
public sealed class JiraSiteRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public JiraSiteRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(JiraSite site)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO jira_site (id, cloud_id, site_url) VALUES (1, @c, @u)
              ON CONFLICT(id) DO UPDATE SET cloud_id=excluded.cloud_id, site_url=excluded.site_url",
            new { c = site.CloudId, u = site.SiteUrl });
    }

    public JiraSite? Get()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<JiraSiteRow>(
            "SELECT cloud_id, site_url FROM jira_site WHERE id = 1");
        return row is null ? null : new JiraSite(row.cloud_id, row.site_url);
    }

    private sealed class JiraSiteRow
    {
        public string cloud_id { get; set; } = "";
        public string site_url { get; set; } = "";
    }
}
