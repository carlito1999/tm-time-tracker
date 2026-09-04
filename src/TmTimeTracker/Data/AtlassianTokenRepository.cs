using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

public sealed record AtlassianCredential(string Email, string Token);

/// <summary>
/// An Atlassian account email plus API token, encrypted with the same DPAPI protector used for the
/// OAuth and Slack tokens.
///
/// Jira and Bitbucket need two DIFFERENT tokens from the same page, and they are not
/// interchangeable: Jira's dev-status accepts a scopeless token, while Bitbucket rejects it with
/// "API Token provided has no Bitbucket scopes" and needs one carrying read:pullrequest:bitbucket.
/// They are stored in separate rows so either can be replaced or revoked on its own.
/// </summary>
public abstract class AtlassianTokenRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    // Interpolated into SQL below, which is safe only because every value comes from the sealed
    // subclasses in this file - never from configuration or user input.
    private readonly string _table;

    protected AtlassianTokenRepository(
        ISqliteConnectionFactory factory, ITokenProtector protector, string table)
    {
        _factory = factory;
        _protector = protector;
        _table = table;
    }

    public void Save(string email, string token)
    {
        using var conn = _factory.Open();
        conn.Execute(
            $@"INSERT INTO {_table} (id, email, token_dpapi) VALUES (1, @e, @t)
               ON CONFLICT(id) DO UPDATE SET email = excluded.email,
                                             token_dpapi = excluded.token_dpapi",
            new { e = email, t = _protector.Protect(token) });
    }

    public AtlassianCredential? Get()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>($"SELECT email, token_dpapi FROM {_table} WHERE id = 1");
        return row is null ? null : new AtlassianCredential(row.email, _protector.Unprotect(row.token_dpapi));
    }

    public void Clear()
    {
        using var conn = _factory.Open();
        conn.Execute($"DELETE FROM {_table} WHERE id = 1");
    }

    private sealed class Row
    {
        public string email { get; set; } = "";
        public byte[] token_dpapi { get; set; } = Array.Empty<byte>();
    }
}

/// <summary>Reads Jira's dev-status API. Needs no scopes.</summary>
public sealed class JiraApiTokenRepository : AtlassianTokenRepository
{
    public JiraApiTokenRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
        : base(factory, protector, "jira_api_token") { }
}

/// <summary>Reads the Bitbucket REST API. Needs the read:pullrequest:bitbucket scope.</summary>
public sealed class BitbucketApiTokenRepository : AtlassianTokenRepository
{
    public BitbucketApiTokenRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
        : base(factory, protector, "bitbucket_api_token") { }
}

/// <summary>
/// Reads the GitLab REST API. Needs a Personal Access Token carrying read_api.
///
/// The email column holds the GitLab username instead of an address: GitLab authenticates with
/// the token alone, and the settings page needs something to show back.
/// </summary>
public sealed class GitLabApiTokenRepository : AtlassianTokenRepository
{
    public GitLabApiTokenRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
        : base(factory, protector, "gitlab_api_token") { }
}
