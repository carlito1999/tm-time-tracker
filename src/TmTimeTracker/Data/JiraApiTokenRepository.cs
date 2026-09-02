using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

public sealed record JiraApiCredential(string Email, string Token);

/// <summary>
/// Stores the Atlassian account email and API token used for Jira's dev-status API.
///
/// Deliberately separate from <see cref="OAuthStateRepository"/>: this is a different credential
/// with a different lifecycle. The OAuth token is minted by a consent flow and refreshes itself;
/// this one is typed in by hand and lives until it is revoked. dev-status accepts only this one -
/// it rejects OAuth 3LO tokens outright, whatever scopes they carry.
/// </summary>
public sealed class JiraApiTokenRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public JiraApiTokenRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(string email, string token)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO jira_api_token (id, email, token_dpapi) VALUES (1, @e, @t)
              ON CONFLICT(id) DO UPDATE SET email = excluded.email,
                                            token_dpapi = excluded.token_dpapi",
            new { e = email, t = _protector.Protect(token) });
    }

    public JiraApiCredential? Get()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT email, token_dpapi FROM jira_api_token WHERE id = 1");
        return row is null ? null : new JiraApiCredential(row.email, _protector.Unprotect(row.token_dpapi));
    }

    public void Clear()
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM jira_api_token WHERE id = 1");
    }

    private sealed class Row
    {
        public string email { get; set; } = "";
        public byte[] token_dpapi { get; set; } = Array.Empty<byte>();
    }
}
