using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

/// <summary>
/// Stores the Slack user token (xoxp-), encrypted with the same DPAPI protector used for the
/// Atlassian OAuth tokens so no plaintext credential ever reaches the database file.
/// </summary>
public sealed class SlackCredentialRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public SlackCredentialRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(string token)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO slack_credential (id, token_dpapi) VALUES (1, @t)
              ON CONFLICT(id) DO UPDATE SET token_dpapi = excluded.token_dpapi",
            new { t = _protector.Protect(token) });
    }

    public string? Get()
    {
        using var conn = _factory.Open();
        var blob = conn.QueryFirstOrDefault<byte[]>(
            "SELECT token_dpapi FROM slack_credential WHERE id = 1");
        return blob is null ? null : _protector.Unprotect(blob);
    }

    public void Clear()
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM slack_credential WHERE id = 1");
    }
}
